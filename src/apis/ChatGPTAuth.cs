using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace LiveCaptionsTranslator.apis
{
    /*
     * Sign in with ChatGPT (ChatGPT plan usage for open-source apps).
     * See https://developers.openai.com/siwc/token-sharing-open-source
     *
     * Flow: OAuth 2.0 Authorization Code + PKCE with a 127.0.0.1 loopback callback.
     * The first sign-in registers a client with `dynamic_agent_client` and saves the issued `client_id`;
     * later sign-ins reuse it. Credentials are encrypted with Windows DPAPI (current user only).
     */
    public static class ChatGPTAuth
    {
        public const string ISSUER = "https://auth.openai.com";
        public const string AUTHORIZE_ENDPOINT = "https://auth.openai.com/api/accounts/authorize";
        public const string TOKEN_ENDPOINT = "https://auth.openai.com/api/accounts/oauth/token";
        public const string DISCOVERY_ENDPOINT = "https://auth.openai.com/.well-known/openid-configuration";
        public const string JWKS_ENDPOINT = "https://auth.openai.com/.well-known/jwks.json";
        public const string RESOURCE = "https://api.openai.com/v1";
        public const string USAGE_SETTINGS_URL = "https://chatgpt.com/settings/usage";

        public const string PLAN_USAGE_SCOPE = "chatgpt.tokens.use.direct";
        private const string SCOPES = "openid profile email offline_access resource.invoke " + PLAN_USAGE_SCOPE;
        private const string DYNAMIC_CLIENT_ID = "dynamic_agent_client";
        private const string AGENT_NAME = "LiveCaptions Translator";
        private const string CALLBACK_PATH = "/auth/callback";
        private const int DEFAULT_CALLBACK_PORT = 1455;

        private static readonly string[] UNUSABLE_REFRESH_ERRORS =
        [
            "invalid_grant", "invalid_refresh_token", "token_expired",
            "refresh_token_expired", "refresh_token_invalidated", "refresh_token_reused"
        ];

        private static readonly string STORAGE_DIR = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveCaptionsTranslator", "chatgpt");
        private static readonly string CREDENTIAL_PATH = Path.Combine(STORAGE_DIR, "credential.bin");
        private static readonly string HOST_ID_PATH = Path.Combine(STORAGE_DIR, "host_id");
        private static readonly byte[] DPAPI_ENTROPY = Encoding.UTF8.GetBytes("LiveCaptionsTranslator.ChatGPT");

        private static readonly HttpClient client = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        private static readonly SemaphoreSlim refreshLock = new(1, 1);
        private static readonly object credentialLock = new();

        private static ChatGPTCredential? credential = LoadCredential();
        private static JsonDocument? jwksCache = null;
        private static CancellationTokenSource? signInCTS = null;

        public static event Action? StateChanged;

        public static bool IsSigningIn => signInCTS != null;
        public static bool IsSignedIn => !string.IsNullOrEmpty(credential?.RefreshToken) ||
                                          !string.IsNullOrEmpty(credential?.AccessToken);
        public static bool IsPlanUsageEnabled => IsSignedIn && credential!.Scopes.Contains(PLAN_USAGE_SCOPE);
        public static bool HasSavedRegistration => !string.IsNullOrEmpty(credential?.ClientId);
        public static string? Email => credential?.Email;

        /// <summary>
        /// Opens the system browser for Sign in with ChatGPT and waits for the loopback callback.
        /// Returns true if this is the first time ChatGPT plan usage was enabled on this host.
        /// </summary>
        /// <param name="newAccount">Register a new client instead of reusing the saved one (switch account).</param>
        /// <param name="forceConsent">Ask for consent again, e.g. after the user declined ChatGPT plan usage.</param>
        public static async Task<bool> SignInAsync(bool newAccount = false, bool forceConsent = false)
        {
            if (signInCTS != null)
                throw new ChatGPTAuthException("A sign-in is already in progress.");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            signInCTS = cts;
            StateChanged?.Invoke();
            try
            {
                return await SignInCoreAsync(newAccount, forceConsent, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new ChatGPTAuthException("Sign-in was canceled or timed out.");
            }
            finally
            {
                signInCTS = null;
                StateChanged?.Invoke();
            }
        }

        public static void CancelSignIn()
        {
            signInCTS?.Cancel();
        }

        private static async Task<bool> SignInCoreAsync(bool newAccount, bool forceConsent, CancellationToken token)
        {
            var saved = credential;
            bool isNewRegistration = newAccount || string.IsNullOrEmpty(saved?.ClientId);
            string requestClientId = isNewRegistration ? DYNAMIC_CLIENT_ID : saved!.ClientId!;
            string hostId = GetOrCreateHostId();

            string state = RandomUrlSafe(32);
            string nonce = RandomUrlSafe(32);
            string codeVerifier = RandomUrlSafe(64);
            string codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

            var listener = StartLoopbackListener();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string redirectUri = $"http://127.0.0.1:{port}{CALLBACK_PATH}";

                var query = new List<KeyValuePair<string, string>>
                {
                    new("client_id", requestClientId),
                    new("response_type", "code"),
                    new("redirect_uri", redirectUri),
                    new("scope", SCOPES),
                    new("resource", RESOURCE),
                    new("state", state),
                    new("nonce", nonce),
                    new("code_challenge_method", "S256"),
                    new("code_challenge", codeChallenge),
                    new("ext_agent_host_id", hostId),
                };
                if (isNewRegistration)
                    query.Add(new("agent_name_hint", AGENT_NAME));
                else
                {
                    if (!string.IsNullOrEmpty(saved!.IdToken))
                        query.Add(new("id_token_hint", saved.IdToken));
                    if (!string.IsNullOrEmpty(saved.Email))
                        query.Add(new("login_hint", saved.Email));
                }
                if (forceConsent)
                    query.Add(new("prompt", "consent"));

                string authorizeUrl = AUTHORIZE_ENDPOINT + "?" + string.Join("&",
                    query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
                Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

                var callback = await WaitForCallbackAsync(listener, state, token);

                // Validate the callback
                if (callback["error"] is string error)
                {
                    if (error == "access_denied")
                        throw new ChatGPTAuthException("Sign-in was declined in the browser.");
                    throw new ChatGPTAuthException($"Sign-in failed: {error} {callback["error_description"]}".Trim());
                }
                string? code = callback["code"];
                if (string.IsNullOrEmpty(code))
                    throw new ChatGPTAuthException("Sign-in failed: no authorization code was returned.");

                string clientId;
                string? callbackClientId = callback["client_id"];
                if (isNewRegistration)
                {
                    if (string.IsNullOrEmpty(callbackClientId) || callbackClientId == DYNAMIC_CLIENT_ID)
                        throw new ChatGPTAuthException("Sign-in failed: client registration was not completed.");
                    clientId = callbackClientId;
                }
                else
                {
                    if (!string.IsNullOrEmpty(callbackClientId) && callbackClientId != requestClientId)
                        throw new ChatGPTAuthException("Sign-in failed: the returned client does not match the saved account.");
                    clientId = requestClientId;
                }

                // Exchange the code
                var tokenResponse = await PostTokenAsync(new Dictionary<string, string>
                {
                    { "grant_type", "authorization_code" },
                    { "client_id", clientId },
                    { "code", code },
                    { "code_verifier", codeVerifier },
                    { "redirect_uri", redirectUri },
                    { "resource", RESOURCE },
                }, token);
                if (!tokenResponse.IsSuccess)
                    throw new ChatGPTAuthException($"Sign-in failed: token exchange returned {tokenResponse.Describe()}.");
                var tokens = tokenResponse.Tokens!;
                if (string.IsNullOrEmpty(tokens.access_token) || string.IsNullOrEmpty(tokens.id_token))
                    throw new ChatGPTAuthException("Sign-in failed: the token response is incomplete.");

                // Validate the ID token
                var identity = await ValidateIdTokenAsync(tokens.id_token, clientId, nonce, token);
                if (!isNewRegistration && !string.IsNullOrEmpty(saved!.Subject) && saved.Subject != identity.Subject)
                    throw new ChatGPTAuthException(
                        "You signed in with a different ChatGPT account. Use \"Use a different account\" to switch accounts.");

                bool wasWelcomed = !isNewRegistration && saved!.WelcomeShown;
                var newCredential = new ChatGPTCredential
                {
                    Email = identity.Email,
                    Subject = identity.Subject,
                    ClientId = clientId,
                    HostId = hostId,
                    IdToken = tokens.id_token,
                    AccessToken = tokens.access_token,
                    RefreshToken = tokens.refresh_token,
                    TokenType = tokens.token_type ?? "Bearer",
                    ExpiresIn = tokens.expires_in > 0 ? tokens.expires_in : 3600,
                    Scopes = ParseScopes(tokens.scope ?? callback["scope"]),
                    SavedAt = DateTimeOffset.UtcNow,
                    WelcomeShown = wasWelcomed,
                };

                bool showWelcome = newCredential.Scopes.Contains(PLAN_USAGE_SCOPE) && !newCredential.WelcomeShown;
                if (showWelcome)
                    newCredential.WelcomeShown = true;
                SaveCredential(newCredential);
                return showWelcome;
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// Returns a valid access token, refreshing it when it is close to expiry.
        /// </summary>
        public static async Task<string> GetAccessTokenAsync(CancellationToken token = default)
        {
            var current = credential;
            if (current == null || !IsSignedIn)
                throw new ChatGPTAuthException("Not signed in to ChatGPT. Sign in in API Setting → ChatGPT.", requiresSignIn: true);
            if (!current.Scopes.Contains(PLAN_USAGE_SCOPE))
                throw new ChatGPTAuthException(
                    "ChatGPT plan usage is not enabled. Click \"Enable plan usage\" in API Setting → ChatGPT.", requiresSignIn: true);
            if (!string.IsNullOrEmpty(current.AccessToken) && current.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(2))
                return current.AccessToken;

            // Serialize refreshes: the refresh token rotates on every use.
            await refreshLock.WaitAsync(token);
            try
            {
                current = credential;
                if (current == null || !IsSignedIn)
                    throw new ChatGPTAuthException("Not signed in to ChatGPT. Sign in in API Setting → ChatGPT.", requiresSignIn: true);
                if (!string.IsNullOrEmpty(current.AccessToken) && current.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(2))
                    return current.AccessToken;
                if (string.IsNullOrEmpty(current.RefreshToken))
                {
                    ClearTokens();
                    throw new ChatGPTAuthException("ChatGPT sign-in expired. Sign in again in API Setting → ChatGPT.", requiresSignIn: true);
                }

                TokenResult result;
                try
                {
                    result = await PostTokenAsync(new Dictionary<string, string>
                    {
                        { "grant_type", "refresh_token" },
                        { "client_id", current.ClientId! },
                        { "refresh_token", current.RefreshToken },
                        { "resource", RESOURCE },
                    }, token);
                }
                catch (HttpRequestException ex)
                {
                    // Temporary network failure: keep credentials, use the old token while it is still valid.
                    if (!string.IsNullOrEmpty(current.AccessToken) && current.ExpiresAt > DateTimeOffset.UtcNow)
                        return current.AccessToken;
                    throw new ChatGPTAuthException($"Could not refresh ChatGPT sign-in: {ex.Message}");
                }

                if (!result.IsSuccess)
                {
                    if (result.ErrorCode != null && UNUSABLE_REFRESH_ERRORS.Contains(result.ErrorCode))
                    {
                        ClearTokens();
                        throw new ChatGPTAuthException("ChatGPT sign-in expired or was disconnected. Sign in again in API Setting → ChatGPT.",
                            requiresSignIn: true);
                    }
                    if (!string.IsNullOrEmpty(current.AccessToken) && current.ExpiresAt > DateTimeOffset.UtcNow)
                        return current.AccessToken;
                    throw new ChatGPTAuthException($"Could not refresh ChatGPT sign-in: {result.Describe()}.");
                }

                var tokens = result.Tokens!;
                if (string.IsNullOrEmpty(tokens.access_token))
                    throw new ChatGPTAuthException("Could not refresh ChatGPT sign-in: the token response is incomplete.");

                // Replace the access token, expiry, granted scopes and rotating refresh token together.
                var refreshed = current.Clone();
                refreshed.AccessToken = tokens.access_token;
                refreshed.RefreshToken = string.IsNullOrEmpty(tokens.refresh_token) ? current.RefreshToken : tokens.refresh_token;
                refreshed.ExpiresIn = tokens.expires_in > 0 ? tokens.expires_in : 3600;
                refreshed.TokenType = tokens.token_type ?? current.TokenType;
                if (!string.IsNullOrEmpty(tokens.scope))
                    refreshed.Scopes = ParseScopes(tokens.scope);
                if (!string.IsNullOrEmpty(tokens.id_token))
                    refreshed.IdToken = tokens.id_token;
                refreshed.SavedAt = DateTimeOffset.UtcNow;
                SaveCredential(refreshed);

                return refreshed.AccessToken;
            }
            finally
            {
                refreshLock.Release();
            }
        }

        /// <summary>
        /// Forces the next `GetAccessTokenAsync` to refresh, e.g. after the API rejected the access token.
        /// </summary>
        public static void InvalidateAccessToken()
        {
            lock (credentialLock)
            {
                if (credential != null)
                    credential.ExpiresIn = 0;
            }
        }

        /// <summary>
        /// Revokes the renewable session and clears local tokens.
        /// The issued client ID and host ID are kept for a later sign-in.
        /// Returns false if remote revocation could not be confirmed.
        /// </summary>
        public static async Task<bool> SignOutAsync()
        {
            bool revoked = false;
            var current = credential;

            if (!string.IsNullOrEmpty(current?.RefreshToken) && !string.IsNullOrEmpty(current.ClientId))
            {
                for (int attempt = 0; attempt < 3 && !revoked; attempt++)
                {
                    if (attempt > 0)
                        await Task.Delay(TimeSpan.FromSeconds(attempt));
                    try
                    {
                        string endpoint = await GetRevocationEndpointAsync();
                        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            { "token", current.RefreshToken },
                            { "token_type_hint", "refresh_token" },
                            { "client_id", current.ClientId },
                        });
                        using var response = await client.PostAsync(endpoint, content);
                        if (response.IsSuccessStatusCode)
                            revoked = true;
                        else if ((int)response.StatusCode < 500)
                            break;
                    }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) { }
                }
            }
            else
                revoked = true;

            ClearTokens();
            return revoked;
        }

        private static async Task<string> GetRevocationEndpointAsync()
        {
            using var response = await client.GetAsync(DISCOVERY_ENDPOINT);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("revocation_endpoint", out var endpoint) &&
                endpoint.GetString() is string url && !string.IsNullOrEmpty(url))
                return url;
            throw new HttpRequestException("The OpenID configuration has no revocation endpoint.");
        }

        #region Loopback callback

        private static TcpListener StartLoopbackListener()
        {
            // Prefer the documented default port; only the port may vary between sign-ins.
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, DEFAULT_CALLBACK_PORT);
                listener.Start();
                return listener;
            }
            catch (SocketException)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                return listener;
            }
        }

        private static async Task<NameValueCollection> WaitForCallbackAsync(
            TcpListener listener, string expectedState, CancellationToken token)
        {
            while (true)
            {
                using var tcpClient = await listener.AcceptTcpClientAsync(token);
                using var stream = tcpClient.GetStream();

                string? target;
                try
                {
                    using var readCTS = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readCTS.CancelAfter(TimeSpan.FromSeconds(10));
                    target = await ReadRequestTargetAsync(stream, readCTS.Token);
                }
                catch (Exception ex) when (ex is IOException || (ex is OperationCanceledException && !token.IsCancellationRequested))
                {
                    continue;
                }
                if (target == null || !target.StartsWith('/'))
                    continue;

                var uri = new Uri("http://127.0.0.1" + target);
                if (uri.AbsolutePath != CALLBACK_PATH)
                {
                    await WriteResponseAsync(stream, "404 Not Found", "Not Found", token);
                    continue;
                }

                var query = HttpUtility.ParseQueryString(uri.Query);
                // Ignore stray requests that do not belong to this attempt.
                if (query["state"] != expectedState)
                {
                    await WriteResponseAsync(stream, "400 Bad Request",
                        CallbackPage("Sign-in failed", "This sign-in request is invalid or has expired. Please try again from the app."), token);
                    continue;
                }

                bool isSuccess = query["error"] == null && query["code"] != null;
                await WriteResponseAsync(stream, "200 OK", isSuccess
                    ? CallbackPage("Signed in with ChatGPT", "You can close this tab and return to LiveCaptions Translator.")
                    : CallbackPage("Sign-in was not completed", "You can close this tab and return to LiveCaptions Translator."), token);
                return query;
            }
        }

        private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total), token);
                if (read == 0)
                    break;
                total += read;
                if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n"))
                    break;
            }

            string request = Encoding.ASCII.GetString(buffer, 0, total);
            int lineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
            if (lineEnd <= 0)
                return null;
            var parts = request[..lineEnd].Split(' ');
            if (parts.Length < 3 || parts[0] != "GET")
                return null;
            return parts[1];
        }

        private static async Task WriteResponseAsync(NetworkStream stream, string status, string body, CancellationToken token)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string header = $"HTTP/1.1 {status}\r\n" +
                            "Content-Type: text/html; charset=utf-8\r\n" +
                            $"Content-Length: {bodyBytes.Length}\r\n" +
                            "Cache-Control: no-store\r\n" +
                            "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
            await stream.WriteAsync(bodyBytes, token);
            await stream.FlushAsync(token);
        }

        private static string CallbackPage(string title, string message)
        {
            return "<!doctype html><html><head><meta charset=\"utf-8\"><title>LiveCaptions Translator</title>" +
                   "<style>body{font-family:'Segoe UI',sans-serif;display:flex;align-items:center;justify-content:center;" +
                   "height:100vh;margin:0;background:#f5f5f5;color:#111}div{text-align:center}" +
                   "@media(prefers-color-scheme:dark){body{background:#1f1f1f;color:#eee}}</style></head>" +
                   $"<body><div><h2>{WebUtility.HtmlEncode(title)}</h2><p>{WebUtility.HtmlEncode(message)}</p></div></body></html>";
        }

        #endregion

        #region Tokens and ID token validation

        private static async Task<TokenResult> PostTokenAsync(Dictionary<string, string> form, CancellationToken token)
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await client.PostAsync(TOKEN_ENDPOINT, content, token);
            string body = await response.Content.ReadAsStringAsync(token);

            if (response.IsSuccessStatusCode)
            {
                var tokens = JsonSerializer.Deserialize<TokenResponse>(body);
                return new TokenResult { StatusCode = (int)response.StatusCode, Tokens = tokens };
            }

            string? errorCode = null, errorDescription = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.String)
                        errorCode = err.GetString();
                    else if (err.ValueKind == JsonValueKind.Object)
                    {
                        if (err.TryGetProperty("code", out var c)) errorCode = c.GetString();
                        if (err.TryGetProperty("message", out var m)) errorDescription = m.GetString();
                    }
                }
                if (root.TryGetProperty("error_description", out var desc))
                    errorDescription = desc.GetString();
            }
            catch (JsonException) { }

            return new TokenResult
            {
                StatusCode = (int)response.StatusCode,
                ErrorCode = errorCode,
                ErrorDescription = errorDescription
            };
        }

        private static async Task<(string Subject, string? Email)> ValidateIdTokenAsync(
            string idToken, string clientId, string expectedNonce, CancellationToken token)
        {
            var parts = idToken.Split('.');
            if (parts.Length != 3)
                throw new ChatGPTAuthException("Sign-in failed: malformed ID token.");

            using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            string? alg = header.RootElement.TryGetProperty("alg", out var algProp) ? algProp.GetString() : null;
            string? kid = header.RootElement.TryGetProperty("kid", out var kidProp) ? kidProp.GetString() : null;
            if (alg != "RS256")
                throw new ChatGPTAuthException($"Sign-in failed: unsupported ID token algorithm ({alg}).");

            var key = await FindSigningKeyAsync(kid, refresh: false, token) ??
                      await FindSigningKeyAsync(kid, refresh: true, token);
            if (key == null)
                throw new ChatGPTAuthException("Sign-in failed: the ID token signing key was not found.");

            using (var rsa = RSA.Create())
            {
                rsa.ImportParameters(key.Value);
                bool isValid = rsa.VerifyData(
                    Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64UrlDecode(parts[2]),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                if (!isValid)
                    throw new ChatGPTAuthException("Sign-in failed: invalid ID token signature.");
            }

            using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var claims = payload.RootElement;

            string? iss = claims.TryGetProperty("iss", out var issProp) ? issProp.GetString() : null;
            if (iss != ISSUER)
                throw new ChatGPTAuthException("Sign-in failed: unexpected ID token issuer.");

            bool audienceMatches = false;
            if (claims.TryGetProperty("aud", out var aud))
            {
                if (aud.ValueKind == JsonValueKind.String)
                    audienceMatches = aud.GetString() == clientId;
                else if (aud.ValueKind == JsonValueKind.Array)
                    audienceMatches = aud.EnumerateArray().Any(a => a.GetString() == clientId);
            }
            if (!audienceMatches)
                throw new ChatGPTAuthException("Sign-in failed: the ID token was issued for another client.");

            if (!claims.TryGetProperty("exp", out var exp) ||
                DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) < DateTimeOffset.UtcNow.AddMinutes(-1))
                throw new ChatGPTAuthException("Sign-in failed: the ID token has expired.");

            string? nonce = claims.TryGetProperty("nonce", out var nonceProp) ? nonceProp.GetString() : null;
            if (nonce != expectedNonce)
                throw new ChatGPTAuthException("Sign-in failed: ID token nonce mismatch.");

            string? sub = claims.TryGetProperty("sub", out var subProp) ? subProp.GetString() : null;
            if (string.IsNullOrEmpty(sub))
                throw new ChatGPTAuthException("Sign-in failed: the ID token has no subject.");
            string? email = claims.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;

            return (sub, email);
        }

        private static async Task<RSAParameters?> FindSigningKeyAsync(string? kid, bool refresh, CancellationToken token)
        {
            if (jwksCache == null || refresh)
            {
                using var response = await client.GetAsync(JWKS_ENDPOINT, token);
                response.EnsureSuccessStatusCode();
                var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var old = Interlocked.Exchange(ref jwksCache, doc);
                old?.Dispose();
            }

            if (!jwksCache!.RootElement.TryGetProperty("keys", out var keys))
                return null;
            foreach (var key in keys.EnumerateArray())
            {
                string? kty = key.TryGetProperty("kty", out var ktyProp) ? ktyProp.GetString() : null;
                string? keyId = key.TryGetProperty("kid", out var kidProp) ? kidProp.GetString() : null;
                if (kty != "RSA" || (kid != null && keyId != kid))
                    continue;
                if (!key.TryGetProperty("n", out var n) || !key.TryGetProperty("e", out var e))
                    continue;
                return new RSAParameters
                {
                    Modulus = Base64UrlDecode(n.GetString()!),
                    Exponent = Base64UrlDecode(e.GetString()!)
                };
            }
            return null;
        }

        private static List<string> ParseScopes(string? scope)
        {
            return string.IsNullOrWhiteSpace(scope)
                ? []
                : scope.Split([' ', '+'], StringSplitOptions.RemoveEmptyEntries).Distinct().Order().ToList();
        }

        #endregion

        #region Credential storage

        private static string GetOrCreateHostId()
        {
            try
            {
                if (File.Exists(HOST_ID_PATH))
                {
                    string existing = File.ReadAllText(HOST_ID_PATH).Trim();
                    if (existing.StartsWith("urn:uuid:"))
                        return existing;
                }
            }
            catch (IOException) { }

            string hostId = $"urn:uuid:{Guid.NewGuid():D}";
            Directory.CreateDirectory(STORAGE_DIR);
            WriteAtomically(HOST_ID_PATH, Encoding.UTF8.GetBytes(hostId));
            return hostId;
        }

        private static ChatGPTCredential? LoadCredential()
        {
            try
            {
                if (!File.Exists(CREDENTIAL_PATH))
                    return null;
                byte[] encrypted = File.ReadAllBytes(CREDENTIAL_PATH);
                byte[] json = ProtectedData.Unprotect(encrypted, DPAPI_ENTROPY, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<ChatGPTCredential>(json);
            }
            catch (Exception ex) when (ex is IOException || ex is CryptographicException || ex is JsonException)
            {
                return null;
            }
        }

        private static void SaveCredential(ChatGPTCredential newCredential)
        {
            lock (credentialLock)
            {
                Directory.CreateDirectory(STORAGE_DIR);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(newCredential);
                byte[] encrypted = ProtectedData.Protect(json, DPAPI_ENTROPY, DataProtectionScope.CurrentUser);
                WriteAtomically(CREDENTIAL_PATH, encrypted);
                credential = newCredential;
            }
            StateChanged?.Invoke();
        }

        private static void ClearTokens()
        {
            var current = credential;
            if (current == null)
                return;
            // Keep the account/client mapping for a later sign-in.
            var cleared = current.Clone();
            cleared.IdToken = null;
            cleared.AccessToken = null;
            cleared.RefreshToken = null;
            cleared.ExpiresIn = 0;
            SaveCredential(cleared);
        }

        private static void WriteAtomically(string path, byte[] content)
        {
            string tempPath = path + ".tmp";
            File.WriteAllBytes(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }

        #endregion

        #region Helpers

        private static string RandomUrlSafe(int byteCount)
        {
            return Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static byte[] Base64UrlDecode(string text)
        {
            string base64 = text.Replace('-', '+').Replace('_', '/');
            base64 += (base64.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            return Convert.FromBase64String(base64);
        }

        public static AuthenticationHeaderValue BearerHeader(string accessToken)
        {
            return new AuthenticationHeaderValue("Bearer", accessToken);
        }

        #endregion

        private class TokenResponse
        {
            public string? access_token { get; set; }
            public string? refresh_token { get; set; }
            public string? id_token { get; set; }
            public string? token_type { get; set; }
            public int expires_in { get; set; }
            public string? scope { get; set; }
        }

        private class TokenResult
        {
            public int StatusCode { get; set; }
            public TokenResponse? Tokens { get; set; }
            public string? ErrorCode { get; set; }
            public string? ErrorDescription { get; set; }
            public bool IsSuccess => Tokens != null;

            public string Describe()
            {
                string text = $"HTTP {StatusCode}";
                if (!string.IsNullOrEmpty(ErrorCode))
                    text += $" {ErrorCode}";
                if (!string.IsNullOrEmpty(ErrorDescription))
                    text += $" ({ErrorDescription})";
                return text;
            }
        }
    }

    public class ChatGPTCredential
    {
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("issuer")] public string Issuer { get; set; } = ChatGPTAuth.ISSUER;
        [JsonPropertyName("subject")] public string? Subject { get; set; }
        [JsonPropertyName("client_id")] public string? ClientId { get; set; }
        [JsonPropertyName("ext_agent_host_id")] public string? HostId { get; set; }
        [JsonPropertyName("id_token")] public string? IdToken { get; set; }
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("token_type")] public string TokenType { get; set; } = "Bearer";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("scopes")] public List<string> Scopes { get; set; } = [];
        [JsonPropertyName("saved_at")] public DateTimeOffset SavedAt { get; set; }
        [JsonPropertyName("welcome_shown")] public bool WelcomeShown { get; set; }

        [JsonIgnore]
        public DateTimeOffset ExpiresAt => SavedAt.AddSeconds(ExpiresIn);

        public ChatGPTCredential Clone()
        {
            var clone = (ChatGPTCredential)MemberwiseClone();
            clone.Scopes = [.. Scopes];
            return clone;
        }
    }

    public class ChatGPTAuthException : Exception
    {
        public bool RequiresSignIn { get; }

        public ChatGPTAuthException(string message, bool requiresSignIn = false) : base(message)
        {
            RequiresSignIn = requiresSignIn;
        }
    }
}

using System.Net.Http;
using System.Text.Json;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis
{
    /// <summary>
    /// Servicio para obtener listas de modelos desde APIs compatibles (LMStudio, Ollama, etc.)
    /// </summary>
    public static class ModelsApiService
    {
        private static readonly HttpClient client = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        /// <summary>
        /// APIs que soportan obtener modelos desde un endpoint.
        /// </summary>
        public static readonly List<string> APIs_WITH_MODELS_ENDPOINT = new()
        {
            "LMStudio",
            "Ollama"
        };

        /// <summary>
        /// Obtiene la URL del endpoint de modelos para una API.
        /// </summary>
        public static string GetModelsEndpoint(string apiName, string baseUrl)
        {
            return apiName switch
            {
                "LMStudio" => TextUtil.NormalizeUrl(baseUrl) + "/models",
                "Ollama" => TextUtil.NormalizeUrl(baseUrl) + "/api/tags",
                _ => null
            };
        }

        /// <summary>
        /// Obtiene la lista de modelos disponibles desde la API.
        /// </summary>
        /// <param name="apiName">Nombre de la API (LMStudio, Ollama, etc.)</param>
        /// <param name="baseUrl">URL base de la API</param>
        /// <returns>Lista de identificadores de modelos para usar en el chat</returns>
        public static async Task<List<ModelInfo>> FetchModelsAsync(string apiName, string baseUrl, CancellationToken token = default)
        {
            string endpoint = GetModelsEndpoint(apiName, baseUrl);
            if (string.IsNullOrEmpty(endpoint))
                return new List<ModelInfo>();

            try
            {
                var response = await client.GetAsync(endpoint, token);
                if (!response.IsSuccessStatusCode)
                    return new List<ModelInfo>();

                string json = await response.Content.ReadAsStringAsync(token);

                return apiName switch
                {
                    "LMStudio" => ParseLMStudioModels(json),
                    "Ollama" => ParseOllamaModels(json),
                    _ => new List<ModelInfo>()
                };
            }
            catch
            {
                return new List<ModelInfo>();
            }
        }

        /// <summary>
        /// Lists the models available to the signed-in ChatGPT account, in server order.
        /// Throws on failure so the caller can show the reason.
        /// </summary>
        public static async Task<List<ModelInfo>> FetchChatGPTModelsAsync(CancellationToken token = default)
        {
            string accessToken = await ChatGPTAuth.GetAccessTokenAsync(token);

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            request.Headers.Authorization = ChatGPTAuth.BearerHeader(accessToken);
            using var response = await client.SendAsync(request, token);
            string json = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 401)
                    ChatGPTAuth.InvalidateAccessToken();
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {(json.Length > 200 ? json[..200] : json)}");
            }

            var result = new List<ModelInfo>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var modelsArray))
                return result;

            foreach (var model in modelsArray.EnumerateArray())
            {
                string visibility = model.TryGetProperty("visibility", out var visProp) ? visProp.GetString() : null;
                if (visibility != "list")
                    continue;

                string slug = model.TryGetProperty("slug", out var slugProp) ? slugProp.GetString() : null;
                if (string.IsNullOrEmpty(slug))
                    continue;

                string displayName = model.TryGetProperty("display_name", out var dnProp) ? dnProp.GetString() : slug;
                result.Add(new ModelInfo { Id = slug, DisplayName = displayName ?? slug });
            }
            return result;
        }

        public class ModelInfo
        {
            public string Id { get; set; }
            public string DisplayName { get; set; }
        }

        private static List<ModelInfo> ParseLMStudioModels(string json)
        {
            var result = new List<ModelInfo>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("models", out var modelsArray))
                    return result;

                foreach (var model in modelsArray.EnumerateArray())
                {
                    string type = model.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                    if (type != "llm")
                        continue;

                    string key = model.TryGetProperty("key", out var keyProp) ? keyProp.GetString() : null;
                    if (string.IsNullOrEmpty(key))
                        continue;

                    string displayName = model.TryGetProperty("display_name", out var dnProp) ? dnProp.GetString() : key;

                    result.Add(new ModelInfo { Id = key, DisplayName = displayName ?? key });
                }
            }
            catch { }

            return result;
        }

        private static List<ModelInfo> ParseOllamaModels(string json)
        {
            var result = new List<ModelInfo>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("models", out var modelsArray))
                    return result;

                foreach (var model in modelsArray.EnumerateArray())
                {
                    string name = model.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    if (string.IsNullOrEmpty(name))
                        continue;

                    result.Add(new ModelInfo { Id = name, DisplayName = name });
                }
            }
            catch { }

            return result;
        }
    }
}

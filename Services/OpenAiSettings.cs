using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace Zapim.Marketing;

public record OpenAiCredentials(string ApiKey, string TextModel, string ImageModel);
public record OpenAiSaved(string ProtectedKey, string TextModel, string ImageModel, DateTime UpdatedAt);

// The OpenAI key and models used for copy and image generation; otherwise read from server configuration (AI:ApiKey,
// AI:TextModel, AI:ImageModel). A blank model means the server default.
public class OpenAiSettings(IConfiguration config, Storage storage, IDataProtectionProvider protection, IHttpClientFactory factory)
    : SavedSettings<OpenAiSaved>(storage, protection, "openai.json", "openai-key-v1")
{
    public string Api => (config["AI:BaseUrl"] is { Length: > 0 } url ? url : "https://api.openai.com/v1").TrimEnd('/');
    public string DefaultTextModel => config["AI:TextModel"] is { Length: > 0 } m ? m : "gpt-4.1-mini";
    public string DefaultImageModel => config["AI:ImageModel"] is { Length: > 0 } m ? m : "gpt-image-1";
    public string Source => Load() is not null ? "app" : config["AI:ApiKey"] is { Length: > 0 } ? "server" : "";
    public DateTime? UpdatedAt => Load()?.UpdatedAt;
    public OpenAiCredentials? Current => Load() is { } s
        ? new(Protector.Unprotect(s.ProtectedKey), s.TextModel is { Length: > 0 } t ? t : DefaultTextModel, s.ImageModel is { Length: > 0 } i ? i : DefaultImageModel)
        : config["AI:ApiKey"] is { Length: > 0 } key ? new(key, DefaultTextModel, DefaultImageModel) : null;
    public bool Configured => Current is not null;

    // A blank key keeps the key already in use. The key and both models are checked with OpenAI before saving.
    public async Task Save(string apiKey, string textModel, string imageModel)
    {
        apiKey = apiKey.Trim(); textModel = textModel.Trim(); imageModel = imageModel.Trim();
        if (apiKey == "") apiKey = Current?.ApiKey ?? "";
        Rules.Require(apiKey.Length is >= 20 and <= 400 && !apiKey.Any(char.IsWhiteSpace), "Enter the OpenAI API key from platform.openai.com/api-keys.");
        foreach (var model in new[] { textModel, imageModel })
            Rules.Require(model == "" || Regex.IsMatch(model, "^[A-Za-z0-9._:-]{1,100}$"), "Enter a valid model name, or leave it blank for the default.");
        foreach (var model in new[] { textModel is { Length: > 0 } ? textModel : DefaultTextModel, imageModel is { Length: > 0 } ? imageModel : DefaultImageModel })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/models/{Uri.EscapeDataString(model)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await factory.CreateClient("external").SendAsync(request);
            Rules.Require((int)response.StatusCode is not 401, "OpenAI rejected this API key.");
            Rules.Require((int)response.StatusCode is not (403 or 404), $"This API key cannot use the model '{model}'.");
            Rules.Require(response.IsSuccessStatusCode, $"OpenAI returned HTTP {(int)response.StatusCode} while checking the key. Try again.", 502);
        }
        Write(new OpenAiSaved(Protector.Protect(apiKey), textModel, imageModel, DateTime.UtcNow));
    }
}

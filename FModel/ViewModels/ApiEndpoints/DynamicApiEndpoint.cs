using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.Utils;
using FModel.Framework;
using FModel.ViewModels.ApiEndpoints.Models;
using RestSharp;
using Serilog;
using Json.Path;

namespace FModel.ViewModels.ApiEndpoints;

public class DynamicApiEndpoint : AbstractApiProvider
{
    public DynamicApiEndpoint(RestClient client) : base(client) { }

    public async Task<AesResponse> GetAesKeysAsync(CancellationToken token, string url, string path)
    {
        var body = await GetRequestBody(token, url).ConfigureAwait(false);
        var jsonPath = JsonPath.Parse(path);
        var tokens = jsonPath.Evaluate(body)?.Matches?.Select(m => m.Value).ToArray() ?? [];

        var ret = new AesResponse { MainKey = Helper.FixKey(tokens.ElementAtOrDefault(0)?.ToString()) };
        if (tokens.ElementAtOrDefault(1) is JsonArray dynamicKeys)
        {
            foreach (var dynamicKey in dynamicKeys)
            {
                if (dynamicKey?["guid"] is not { } guid || dynamicKey["key"] is not { } key)
                    continue;

                ret.DynamicKeys.Add(new DynamicKey
                {
                    Name = dynamicKey["name"]?.ToString(),
                    Guid = guid.ToString(),
                    Key = Helper.FixKey(key.ToString())
                });
            }
        }

        return ret;
    }

    public AesResponse GetAesKeys(CancellationToken token, string url, string path)
    {
        return GetAesKeysAsync(token, url, path).GetAwaiter().GetResult();
    }

    public async Task<MappingsResponse[]> GetMappingsAsync(CancellationToken token, string url, string path)
    {
        var body = await GetRequestBody(token, url).ConfigureAwait(false);
        var jsonPath = JsonPath.Parse(path);
        var tokens = jsonPath.Evaluate(body)?.Matches?.Select(m => m.Value).ToArray() ?? [];

        var ret = new MappingsResponse[] { new() };
        ret[0].Url = tokens.ElementAtOrDefault(0)?.ToString();
        if (tokens.ElementAtOrDefault(1) is not { } fileName)
            fileName = ret[0].Url?.SubstringAfterLast("/");
        ret[0].FileName = fileName.ToString();
        return ret;
    }

    public MappingsResponse[] GetMappings(CancellationToken token, string url, string path)
    {
        return GetMappingsAsync(token, url, path).GetAwaiter().GetResult();
    }

    public async Task<JsonNode> GetRequestBody(CancellationToken token, string url)
    {
        var request = new FRestRequest(url)
        {
            Interceptors = [_interceptor]
        };
        var response = await _client.ExecuteAsync(request, token).ConfigureAwait(false);
        Log.Information("[{Method}] [{Status}({StatusCode})] '{Resource}'", request.Method, response.StatusDescription, (int) response.StatusCode, response.ResponseUri?.OriginalString);
        return response.IsSuccessful && !string.IsNullOrEmpty(response.Content) ? JsonNode.Parse(response.Content) : JsonNode.Parse("{}");
    }
}

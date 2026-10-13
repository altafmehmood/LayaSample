using DocIntake.Api.Services;
using Microsoft.OpenApi;

namespace DocIntake.Api;

internal static class OpenApiSetup
{
    public static IServiceCollection AddGatewayOpenApi(this IServiceCollection services, IConfiguration configuration) =>
        services.AddOpenApi(o =>
        {
            o.AddDocumentTransformer((document, _, _) =>
            {
                // Behind a gateway the request URL is not the public one; OpenApi:ServerUrl says what clients (Scalar) should call.
                var serverUrl = configuration["OpenApi:ServerUrl"];
                if (!string.IsNullOrWhiteSpace(serverUrl))
                    document.Servers = [new() { Url = serverUrl }];

                // Lets Scalar ask for the key; it is attached to the endpoints that need it by the operation transformer below.
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = ApiAccessOptions.HeaderName,
                    Description = "Access key for the endpoints that spend model-provider money."
                };
                return Task.CompletedTask;
            });
            o.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<RequireApiKeyMetadata>().Any())
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", context.Document)] = [] });
                }
                return Task.CompletedTask;
            });
        });
}

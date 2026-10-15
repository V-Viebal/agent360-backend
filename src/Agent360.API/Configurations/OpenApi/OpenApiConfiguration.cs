using Asp.Versioning;
using Hmz.Core.SharedKernel.AdvancedSearch.OpenApi;

namespace Agent360.API.Configurations.OpenApi;

public static class OpenApiConfiguration
{
    extension(IApiVersioningBuilder apiVersioningBuilder)
    {
        public IApiVersioningBuilder ConfigureOpenApi()
        {
            apiVersioningBuilder.AddOpenApi(opts =>
            {
                opts.Document.AddDocumentTransformer<SecuritySchemeTransformer>();
                opts.Document.AddDocumentTransformer<AdvancedSearchOpenApiTransformer>();
            });

            return apiVersioningBuilder;
        }
    }
}
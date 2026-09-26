using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SemanticKernel.MedicalCorrection;

namespace SemanticKernel
{
    public static class ServiceCollectionExtensions
    {
        public const string SettingsFileName = "appsettings.llm.json";

        /// <summary>
        /// Registers <see cref="KernelFactory"/> (singleton, owns the model) and <see cref="IMedicalTermCorrector"/>.
        /// <c>appsettings.llm.json</c> from the output directory supplies the defaults of the <c>Llm</c> section;
        /// values in <paramref name="configuration"/> (appsettings, env vars like <c>Llm__ModelFile</c>) override them.
        /// </summary>
        public static IServiceCollection AddMedicalTermCorrection(this IServiceCollection services, IConfiguration configuration)
        {
            var merged = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, SettingsFileName), optional: true)
                .AddConfiguration(configuration)
                .Build();

            services.AddOptions<LlmOptions>().Bind(merged.GetSection(LlmOptions.SectionName));
            services.AddSingleton<KernelFactory>();
            services.AddSingleton<IChatCompletionProvider>(sp => sp.GetRequiredService<KernelFactory>());
            services.AddSingleton<IMedicalTermCorrector, MedicalTermCorrector>();
            return services;
        }
    }
}

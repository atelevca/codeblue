using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticKernel.MedicalCorrection;
using SemanticKernel.Minutes;

namespace SemanticKernel
{
    public static class ServiceCollectionExtensions
    {
        public const string SettingsFileName = "appsettings.llm.json";

        /// <summary>
        /// Registers the local models, <see cref="IMedicalTermCorrector"/> and <see cref="IMeetingMinutesGenerator"/>.
        /// <c>appsettings.llm.json</c> from the output directory supplies the defaults of the <c>Llm</c> section;
        /// values in <paramref name="configuration"/> (appsettings, env vars like <c>Llm__ModelFile</c>) override them.
        /// The models are keyed by <see cref="LlmModelRole"/>: the corrector gets the correction model, the minutes
        /// generator the minutes model. If <c>Llm:Minutes</c> names the same file (or nothing), one factory serves both,
        /// so the weights are loaded once.
        /// </summary>
        public static IServiceCollection AddMedicalTermCorrection(this IServiceCollection services, IConfiguration configuration)
        {
            var merged = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, SettingsFileName), optional: true)
                .AddConfiguration(configuration)
                .Build();

            services.AddOptions<LlmOptions>().Bind(merged.GetSection(LlmOptions.SectionName));
            services.AddOptions<MinutesOptions>().Bind(merged.GetSection(MinutesOptions.SectionName));

            services.AddKeyedSingleton(LlmModelRole.Correction, (sp, _) => new KernelFactory(
                LlmModelRole.Correction, sp.GetRequiredService<IOptions<LlmOptions>>(), sp.GetRequiredService<ILogger<KernelFactory>>()));
            services.AddKeyedSingleton(LlmModelRole.Minutes, (sp, _) =>
            {
                var options = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
                return options.ModelFor(LlmModelRole.Minutes) == options.ModelFor(LlmModelRole.Correction)
                    ? sp.GetRequiredKeyedService<KernelFactory>(LlmModelRole.Correction)
                    : new KernelFactory(LlmModelRole.Minutes, sp.GetRequiredService<IOptions<LlmOptions>>(),
                        sp.GetRequiredService<ILogger<KernelFactory>>());
            });
            foreach (var role in new[] { LlmModelRole.Correction, LlmModelRole.Minutes })
            {
                services.AddKeyedSingleton<IChatCompletionProvider>(role,
                    (sp, key) => sp.GetRequiredKeyedService<KernelFactory>(key!));
            }

            services.AddSingleton<IMedicalTermCorrector, MedicalTermCorrector>();
            services.AddSingleton<IMeetingMinutesGenerator, MeetingMinutesGenerator>();
            return services;
        }
    }
}

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
        /// generator the minutes model, each as <see cref="IChatCompletionProvider"/> and as the <see cref="ITokenCounter"/>
        /// of that model's context. If <c>Llm:Minutes</c> names the same file (or nothing), one factory serves both,
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

            services.AddKeyedSingleton(LlmModelRole.Correction, (sp, _) => CreateFactory(sp, LlmModelRole.Correction));
            services.AddKeyedSingleton(LlmModelRole.Minutes, (sp, _) =>
                IsShared(sp.GetRequiredService<IOptions<LlmOptions>>().Value)
                    ? sp.GetRequiredKeyedService<KernelFactory>(LlmModelRole.Correction)
                    : CreateFactory(sp, LlmModelRole.Minutes));
            foreach (var role in new[] { LlmModelRole.Correction, LlmModelRole.Minutes })
            {
                services.AddKeyedSingleton<IChatCompletionProvider>(role,
                    (sp, key) => sp.GetRequiredKeyedService<KernelFactory>(key!));
                services.AddKeyedSingleton<ITokenCounter>(role,
                    (sp, key) => sp.GetRequiredKeyedService<KernelFactory>(key!));
            }

            services.AddSingleton<IMedicalTermCorrector, MedicalTermCorrector>();
            services.AddSingleton<IMeetingMinutesGenerator, MeetingMinutesGenerator>();
            return services;
        }

        private static bool IsShared(LlmOptions options) =>
            options.ModelFor(LlmModelRole.Minutes) == options.ModelFor(LlmModelRole.Correction);

        // A shared factory knows both roles, so its context check covers the prompts and replies of both.
        private static KernelFactory CreateFactory(IServiceProvider sp, LlmModelRole role)
        {
            var options = sp.GetRequiredService<IOptions<LlmOptions>>();
            var roles = role == LlmModelRole.Correction && IsShared(options.Value)
                ? new[] { LlmModelRole.Correction, LlmModelRole.Minutes }
                : new[] { role };
            return new KernelFactory(roles, options, sp.GetRequiredService<IOptions<MinutesOptions>>(),
                sp.GetRequiredService<ILogger<KernelFactory>>());
        }
    }
}

using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Audio;

public static class AudioServiceCollectionExtensions
{
    public static IServiceCollection AddAudioStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AudioOptions>()
            .Configure(options =>
            {
                try
                {
                    configuration.GetSection(AudioOptions.SectionName).Bind(options);
                }
                catch (InvalidOperationException)
                {
                    // Binder conversion errors include the supplied value, including in
                    // their inner exception. Replace them before startup can log them.
                    throw new OptionsValidationException(Options.DefaultName, typeof(AudioOptions),
                        [AudioOptions.ConfigurationFailure]);
                }
            })
            .Validate(options => options.IsValid(), AudioOptions.ConfigurationFailure)
            .ValidateOnStart();
        services.AddSingleton<Lexarbor.Domain.Repositories.IAudioObjectStore, S3AudioObjectStore>();
        // Internal only until every audio entry point uses an operation reference.
        services.AddSingleton<IAudioObjectStoreFactory, S3AudioObjectStoreFactory>();
        services.AddSingleton<IAudioDestinationRecords, AudioDestinationRecords>();
        services.AddSingleton<AudioRuntimeConfiguration>();
        return services;
    }
}

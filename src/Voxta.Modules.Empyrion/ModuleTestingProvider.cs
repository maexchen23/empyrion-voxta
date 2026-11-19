using Voxta.Abstractions.Encryption;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Abstractions.Testing;
using Voxta.Modules.Empyrion.Bridge;
using Voxta.Modules.Empyrion.Configuration;

namespace Voxta.Modules.Empyrion;

/// <summary>Voxta's "Test" for this module: calls the bridge with the saved settings.</summary>
public class ModuleTestingProvider(ILocalEncryptionProvider encryptionProvider) : IVoxtaModuleTestingProvider
{
    public async Task<ModuleTestResultItem[]> TestModuleAsync(
        IAuthenticationContext auth,
        Guid moduleId,
        ISettingsSource settings,
        CancellationToken cancellationToken)
    {
        var url = settings.GetOptional(ModuleConfigurationProvider.BridgeUrl);
        if (string.IsNullOrWhiteSpace(url))
            return [new ModuleTestResultItem { Success = false, Message = "Set the bridge URL first." }];

        var token = BridgeSecrets.RevealToken(encryptionProvider, settings.GetOptional(ModuleConfigurationProvider.Token));
        var (ok, text) = await BridgeConnectionTest.RunAsync(
            new BridgeClient(url, token), settings.GetOptional(ModuleConfigurationProvider.SteamId), cancellationToken);
        return [new ModuleTestResultItem { Success = ok, Message = text }];
    }
}

using Voxta.Abstractions.Modules;
using Voxta.Abstractions.Registration;
using Voxta.Modules.Empyrion.ChatAugmentations;
using Voxta.Modules.Empyrion.Configuration;

namespace Voxta.Modules.Empyrion;

public class VoxtaModule : IVoxtaModule
{
    public void Configure(IVoxtaModuleBuilder builder)
    {
        builder.Register(new()
        {
            ServiceName = EmpyrionConstants.ServiceName,
            Label = "Empyrion: Galactic Survival",
            Version = ModuleInfo.Version,
            Flags = [ModuleFlag.Experimental, ModuleFlag.SupportsExplicitContent],
            InstallableBy = ModuleInstallableBy.Anyone,
            Supports = new()
            {
                ChatAugmentations = new(ServiceDefinitionCategoryScore.Medium),
            },
            Pricing = ServiceDefinitionPricing.Free,
            Hosting = ServiceDefinitionHosting.Builtin,
            Notes = "Connects to the VoxtaBridge mod on an Empyrion dedicated server (host:port + token). " +
                    "Tools for the player's location, status and current ship with cargo.",
            Pros = "Live game state, works with any model that can call tools",
            Cons = "Needs the VoxtaBridge server mod; requires a model that supports tool calling",
            Augmentations = EmpyrionConstants.Augmentations,
            // How-tos the model loads when it needs them (Skills/*.md, embedded by the SDK; Voxta 1.13+)
            Skills = ModuleSkills.EmbeddedResources(typeof(VoxtaModule)),
            ModuleConfigurationProviderType = typeof(ModuleConfigurationProvider),
            ModuleConfigurationFieldsRequiringReload = ModuleConfigurationProvider.FieldsRequiringReload,
            ModuleTestingProviderType = typeof(ModuleTestingProvider),
        });

        builder.AddChatAugmentationsService<EmpyrionAugmentationsService>(EmpyrionConstants.ServiceName);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityCli.Protocol;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    public interface IAssetCreateProvider
    {
        AssetCreateTypeDescriptor Describe();

        AssetCreateArtifact Create(AssetCreateRequest request);
    }

    public sealed class AssetCreateRequest
    {
        public AssetCreateRequest(AssetCreateArgs args, string typeId, string assetPath)
        {
            TypeId = typeId;
            AssetPath = assetPath;
            IsForced = args.force;
            ScriptPath = args.script;
            TypeName = args.typeName;
            DataJson = args.dataJson;
            OptionsJson = args.optionsJson;
        }

        public string TypeId { get; private set; }

        public string AssetPath { get; private set; }

        public bool IsForced { get; private set; }

        public string ScriptPath { get; private set; }

        public string TypeName { get; private set; }

        public string DataJson { get; private set; }

        public string OptionsJson { get; private set; }

        public TOptions GetOptions<TOptions>() where TOptions : new()
        {
            return string.IsNullOrWhiteSpace(OptionsJson)
                ? new TOptions()
                : JsonConvert.DeserializeObject<TOptions>(OptionsJson, BridgeJsonSettings.CamelCaseIgnoreNull) ?? new TOptions();
        }
    }

    public sealed class AssetCreateArtifact
    {
        public AssetCreateArtifact(Action saveAction, Type expectedMainAssetType, UnityEngine.Object dataPatchTarget = null)
        {
            if (saveAction == null)
            {
                throw new ArgumentNullException(nameof(saveAction));
            }

            if (expectedMainAssetType == null)
            {
                throw new ArgumentNullException(nameof(expectedMainAssetType));
            }

            SaveAction = saveAction;
            ExpectedMainAssetType = expectedMainAssetType;
            DataPatchTarget = dataPatchTarget;
        }

        public Action SaveAction { get; private set; }

        public Type ExpectedMainAssetType { get; private set; }

        public UnityEngine.Object DataPatchTarget { get; private set; }
    }

    public static class AssetCreateRegistry
    {
        private static readonly Dictionary<string, IAssetCreateProvider> _providers = new Dictionary<string, IAssetCreateProvider>(StringComparer.OrdinalIgnoreCase);
        private static bool _isInitialized;

        public static void Register(IAssetCreateProvider provider)
        {
            EnsureInitialized();
            RegisterInternal(provider);
        }

        public static AssetCreateTypeDescriptor[] GetDescriptors()
        {
            EnsureInitialized();
            return _providers.Values
                .Select(provider => NormalizeDescriptor(provider.Describe()))
                .OrderBy(descriptor => descriptor.typeId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static IAssetCreateProvider Resolve(string typeId)
        {
            EnsureInitialized();

            string normalizedType = string.IsNullOrWhiteSpace(typeId) ? string.Empty : typeId.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalizedType))
            {
                throw new CommandFailureException("ASSET_TYPE_NOT_FOUND", "`asset create --type` requires a value.");
            }

            IAssetCreateProvider provider;
            if (!_providers.TryGetValue(normalizedType, out provider))
            {
                throw new CommandFailureException("ASSET_TYPE_NOT_FOUND", "Unsupported asset create type: " + typeId);
            }

            return provider;
        }

        private static void EnsureInitialized()
        {
            if (_isInitialized)
            {
                return;
            }

            bool succeeded = false;
            try
            {
                foreach (IAssetCreateProvider provider in BuiltInAssetCreateProviders.CreateAll())
                {
                    RegisterInternal(provider);
                }

                succeeded = true;
            }
            finally
            {
                if (succeeded)
                {
                    _isInitialized = true;
                }
                else
                {
                    _providers.Clear();
                }
            }
        }

        private static void RegisterInternal(IAssetCreateProvider provider)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            AssetCreateTypeDescriptor descriptor = NormalizeDescriptor(provider.Describe());
            if (string.IsNullOrWhiteSpace(descriptor.typeId))
            {
                throw new InvalidOperationException("Asset create provider typeId is empty.");
            }

            string normalizedType = descriptor.typeId.Trim().ToLowerInvariant();
            if (_providers.ContainsKey(normalizedType))
            {
                throw new InvalidOperationException("Asset create type is already registered: " + normalizedType);
            }

            _providers.Add(normalizedType, provider);
        }

        private static AssetCreateTypeDescriptor NormalizeDescriptor(AssetCreateTypeDescriptor descriptor)
        {
            descriptor.typeId = string.IsNullOrWhiteSpace(descriptor.typeId)
                ? string.Empty
                : descriptor.typeId.Trim().ToLowerInvariant();
            descriptor.origin = string.IsNullOrWhiteSpace(descriptor.origin)
                ? "extension"
                : descriptor.origin.Trim().ToLowerInvariant();
            descriptor.requiredOptions = descriptor.requiredOptions ?? Array.Empty<string>();
            descriptor.optionalOptions = descriptor.optionalOptions ?? Array.Empty<string>();
            descriptor.aliases = descriptor.aliases ?? Array.Empty<string>();
            descriptor.notes = descriptor.notes ?? Array.Empty<string>();
            descriptor.aliases = descriptor.aliases
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return descriptor;
        }
    }
}

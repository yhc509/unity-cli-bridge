#nullable enable
using System;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed class CustomCommandHandler
    {
        private readonly CustomCommandRegistry _registry = new CustomCommandRegistry();

        public bool CanHandle(string command)
        {
            return string.Equals(command, ProtocolConstants.CommandCustom, StringComparison.Ordinal);
        }

        public string Handle(string command, string argumentsJson)
        {
            CustomCommandArgs args = ProtocolJson.Deserialize<CustomCommandArgs>(argumentsJson) ?? new CustomCommandArgs();
            if (string.IsNullOrWhiteSpace(args.commandName))
            {
                throw new CommandFailureException("INVALID_ARGS", "Custom command name is empty.", false, null);
            }

            string resultJson = _registry.Invoke(args.commandName, args.argumentsJson ?? "{}");

            return ProtocolJson.Serialize(new CustomCommandPayload
            {
                commandName = args.commandName,
                resultJson = resultJson,
            });
        }
    }
}

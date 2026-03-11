// Copyright (c) 2026 @Retype15. Licensed under SOS Custom Permissive License (SCPL).
// See LICENSE file in the project root for full license information.

using Barotrauma;
using Microsoft.Xna.Framework;

namespace SOS
{
    public partial class Plugin : IAssemblyPlugin
    {
        private SOSController? controller;

        public void InitClient()
        {
            controller = new SOSController();

            DebugConsole.commands.Add(new DebugConsole.Command(
                name: "sos",
                help: TextSOS.Get("sos.command.help", "Open/Close SOS.").Value,
                onExecute: _ => CrossThread.RequestExecutionOnMainThread(() => controller?.ToggleUI()),
                getValidArgs: null,
                isCheat: false
            )
            {
                RelayToServer = false, // Client only
                OnClientExecute = _ => CrossThread.RequestExecutionOnMainThread(() => controller?.ToggleUI())
            });

            GameMain.LuaCs.Hook.Add("keyupdate", "SOS_UpdateLoop", _ =>
            {
                controller?.Update();
                return null;
            });

            LuaCsLogger.LogMessage(TextSOS.Get("sos.client.init", "[SOS] Client: Initialized. Press 'J' to open.").Value);
        }

        public void DisposeClient()
        {
            controller?.SaveSettings();
            controller?.Destroy();
            controller = null;
        }
    }
}
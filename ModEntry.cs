using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace StardewGPT
{
    /// <summary>The mod entry point.</summary>
    internal sealed class ModEntry : Mod
    {

  
        private bool hasShownWelcome = false;
        internal List<ChatMessage> SessionHistory { get; } = new();
        private ModConfig Config = new();

        private RAG? rag;


        /*********
        ** Public methods
        *********/
        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {

 
            this.Config = this.Helper.ReadConfig<ModConfig>();

            this.rag = new RAG(this.Config, 2, this.Helper.DirectoryPath);

            helper.Events.Input.ButtonPressed += this.OnButtonPressed;

            helper.Events.GameLoop.ReturnedToTitle += (_, __) =>
            {
                this.hasShownWelcome = false;
                this.SessionHistory.Clear();
            };
        }

    

        /*********
        ** Private methods
        *********/
        /// <summary>Raised after the player presses a button on the keyboard, controller, or mouse.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            
            // ignore if player hasn't loaded a save yet
            if (!Context.IsWorldReady)
                return;

            // ignore if player has already opened menus
            if (Game1.activeClickableMenu != null)
                return;

            // ignore if player press the wrong key
            if(e.Button != this.Config.OpenChatKey)
                return;

            if (Game1.activeClickableMenu is GptMenu &&
                (e.Button == SButton.E || e.Button == SButton.Escape))
            {
                Helper.Input.Suppress(e.Button);
                return;
            }


            // seed once per save session
            if (this.SessionHistory.Count == 0)
                this.SessionHistory.Add(new ChatMessage("Grandpa", "Having trouble?"));




            // open menu using shared history
            Game1.activeClickableMenu = new GptMenu(this.SessionHistory, this.rag!);

            // show welcome once per save session
            if (!this.hasShownWelcome)
            {
                Game1.showGlobalMessage("Prepared to talk to Grandpa");
                this.hasShownWelcome = true;
            }
    
        }
    }
}
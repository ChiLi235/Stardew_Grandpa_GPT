using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace StardewGPT
{
    internal sealed class ModConfig
    {

        public string CHAT_MODEL {get;} = "@cf/openai/gpt-oss-20b";
        public string ACCOUNT_ID {get;}= "TO_BE_REPLACED";
        public string AUTH_API {get;}= "TO_BE_REPLACED";
        public int MAX_TOKEN{get;} = 800;
        public string reasoning_effort{get;} = "medium";

        public float temperature{get;} = 0.2f; 
        public SButton OpenChatKey {get;set;} = SButton.K;
    }
}
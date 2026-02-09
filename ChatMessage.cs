namespace StardewGPT
{
    internal sealed class ChatMessage
    {
        public string Role { get; }
        public string Message { get; set;}

        public ChatMessage(string role, string message)
        {
            Role = role;
            Message = message;
        }
    }
}

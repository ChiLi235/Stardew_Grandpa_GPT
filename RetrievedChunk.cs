namespace StardewGPT
{
    public class RetrievedChunk
    {
            public long PageId {get;set;}
            public int ChunkIndex { get; set; }
            public string Text { get; set; } = "";
            public float Score { get; set; }
    }
}
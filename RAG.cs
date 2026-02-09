using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;


namespace StardewGPT
{
    class RAG
    {

        private string ACCOUNT_ID;
        private string AUTH_API;

        private int MAX_TOKEN;
        private float temperature;

        private string reasoning_effort;
        private string DB_PATH;
        private string EMBEDD_MODEL;
        private string CHAT_MODEL;
        private static readonly HttpClient _http = new HttpClient();
        private string prerequisite;

        private int k = 2;
        public RAG(ModConfig config, int k, string modDir)
        {

            this.ACCOUNT_ID = config.ACCOUNT_ID;
            this.AUTH_API = config.AUTH_API;
            this.MAX_TOKEN = config.MAX_TOKEN;
            this.temperature = config.temperature;
            this.reasoning_effort = config.reasoning_effort;
            this.CHAT_MODEL = config.CHAT_MODEL;

            this.EMBEDD_MODEL = "bge-m3";

            this.DB_PATH = Path.GetFullPath(Path.Combine(modDir, "..", "StardeWiki", "stardew_wiki.sqlite"));

            this.prerequisite =
                "System role: You are Grandpa from Stardew Valley.\n" +
                "Goal: Help the user answer their question.\n" +
                "\n" +
                "Hard rules:\n" +
                "1) Speak in Grandpa's tone.\n" +
                "2) Use ONLY the provided context. No outside knowledge. No guessing.\n" +
                "3) If the context does not contain the answer, reply: I don't know.\n" +
                "4) Keep the answer short and precise. Use bullet points if helpful.\n" +
                "5) Output must be plain text only. No markdown, no HTML, no special formatting symbols.\n" +
                "\n" +
                "Example:\n" +
                "Hey child, the price of wheat seeds is 10g.";


            this.k = k;
        }

        private async Task<float[]> embedd_texts(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Query text is empty.");

            var url = $"https://api.cloudflare.com/client/v4/accounts/{this.ACCOUNT_ID}/ai/run/@cf/baai/{this.EMBEDD_MODEL}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.AUTH_API);
            var payload = new { text = new[] { text } };
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Embedding API error: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");

            using var doc = JsonDocument.Parse(body);

            // Cloudflare response:
            // { "success": true, "result": { "data": [ [..vector..] ] } }
            var result = doc.RootElement.GetProperty("result");
            var data = result.GetProperty("data");

            // Take first vector
            var vecJson = data[0];
            var vec = new float[vecJson.GetArrayLength()];
            int i = 0;
            foreach (var v in vecJson.EnumerateArray())
                vec[i++] = v.GetSingle();

            return vec;
        }




        // Return the top k result from database that close to the embedding. 
        // Cosiane similiarity
        private async Task<List<RetrievedChunk>> top_k (float[] queryVec)
        {
            if (queryVec == null || queryVec.Length == 0)
                throw new ArgumentException("queryVec is empty.");
            
            int modelId;
            int dim;
            using (var conn = openDb())
            {
                (modelId, dim) = GetModelIdAndDim(conn, EMBEDD_MODEL);
            }

            if (queryVec.Length != dim)
                throw new Exception($"Query embedding dim={queryVec.Length} does not match DB dim={dim} for model '{EMBEDD_MODEL}'.");

            float qNorm = L2Norm(queryVec);
            if (qNorm <= 0f) qNorm = 1e-8f;

            var best = new List<RetrievedChunk>(capacity: k);

            using var conn2 = openDb();
            using var cmd = conn2.CreateCommand();
            cmd.CommandText = @"
SELECT
  c.page_id, c.chunk_index, c.text, e.vec, e.norm
FROM embedding e
JOIN chunk c ON c.chunk_id = e.chunk_id
WHERE e.model_id = $modelId
";
            cmd.Parameters.AddWithValue("$modelId", modelId);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                long page_ID = reader.GetInt64(0);
                float dNorm = (float)reader.GetDouble(4);
                byte[] blob = (byte[])reader["vec"];

                // Deserialize float32 blob => float[]
                float[] docVec = BlobToFloat32Array(blob, dim);

                float score = CosineSim(queryVec, qNorm, docVec, dNorm);

                // Maintain a small top-k list
                UpsertTopK(best, k, new RetrievedChunk
                {
                    PageId = page_ID,
                    ChunkIndex = reader.GetInt32(1),
                    Text = reader.GetString(2),
                    Score = score
                });
            }

            // Sort best high->low
            best.Sort((a, b) => b.Score.CompareTo(a.Score));
            return best;

        }

        public async Task<string> GetAnswerAsync(string question)
        {
            var qVec = await embedd_texts(question);
            var top = await top_k(qVec);

            string context = Build_Context(top);
            string prompt = Build_Prompt(question, context);

            // Call LLM (Cloudflare)
            string answer = await CallLLM(prompt);
            return answer;
        }

        private async Task<String> CallLLM(string prompt)
        {
            var url = $"https://api.cloudflare.com/client/v4/accounts/{ACCOUNT_ID}/ai/run/{CHAT_MODEL}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AUTH_API);

            // Many CF LLM models expect: { "messages": [ {role, content}, ... ] }
            var payload = new
            {
                messages = new[]
                {
                    new { role = "system", content = prerequisite },
                    new { role = "user", content = prompt }
                },
                max_tokens = this.MAX_TOKEN,
                temperature = this.temperature,
                reasoning_effort = this.reasoning_effort
            };
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"LLM API error: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");

            using var doc = JsonDocument.Parse(body);

            // CF often returns:
            // { "success": true, "result": { "response": "..." } }
            // Some models return different keys; adjust if needed.
            var root = doc.RootElement;

            // Most CF calls have root.result
            JsonElement result = root.TryGetProperty("result", out var r) ? r : root;

            // 1) Workers AI common: result.response
            if (result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("response", out var response) &&
                response.ValueKind == JsonValueKind.String)
            {
                return response.GetString() ?? "";
            }

            // 2) OpenAI-style: result.choices[0].message.content
            if (result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var choice0 = choices[0];
                if (choice0.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content))
                {
                    // content can be string OR array of parts depending on provider
                    if (content.ValueKind == JsonValueKind.String)
                        return content.GetString() ?? "";

                    if (content.ValueKind == JsonValueKind.Array)
                    {
                        // join text parts if present
                        var sb = new StringBuilder();
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var textPart) && textPart.ValueKind == JsonValueKind.String)
                                sb.Append(textPart.GetString());
                        }
                        return sb.ToString();
                    }
                }
            }

            // 3) Some models: result.output_text
            if (result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("output_text", out var ot) &&
                ot.ValueKind == JsonValueKind.String)
            {
                return ot.GetString() ?? "";
            }

            // fallback: dump result
            return result.ToString();
        }

        private string Build_Context(List<RetrievedChunk> best)
        {

            if (best == null || best.Count == 0)
                return "(No context retrieved.)";

            var sb = new StringBuilder();
            for (int i = 0; i < best.Count; i++)
            {
                var c = best[i];
                sb.AppendLine($"[Context {i + 1}]");
                sb.AppendLine(c.Text);
                sb.AppendLine();
            }
            return sb.ToString();
        }
        private string Build_Prompt(string question, string context)
        {

            return @"Use the context below to answer the quesiton. If the answer is not in the context, just say you don't know.\n"
            + @"==== Context ==== \n"
            + context + "\n"
            + @"==== Question ==== \n"
            + question;
        }

        private void UpsertTopK(List<RetrievedChunk> best, int k, RetrievedChunk cand)
        {
            if (best.Count < k)
            {
                best.Add(cand);
                return;
            }

            // find current worst
            int worstIdx = 0;
            float worstScore = best[0].Score;
            for (int i = 1; i < best.Count; i++)
            {
                if (best[i].Score < worstScore)
                {
                    worstScore = best[i].Score;
                    worstIdx = i;
                }
            }

            if (cand.Score > worstScore)
                best[worstIdx] = cand;
        }
        private SqliteConnection openDb()
        {
            var conn = new SqliteConnection($"Data Source={DB_PATH}");
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
            return conn; 
        }


        private (int modelId, int dim) GetModelIdAndDim(SqliteConnection conn, string modelName)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT model_id, dim FROM embedding_model;";

            using var r = cmd.ExecuteReader();
            if (!r.Read())
                throw new Exception($"No embedding_model row found for name='{modelName}'. Did you build embeddings with the same name?");

            return (r.GetInt32(0), r.GetInt32(1));
        }



        private static float[] BlobToFloat32Array(byte[] blob, int dim)
        {
            // Expect little-endian float32 packed bytes (dim * 4)
            if (blob.Length != dim * 4)
                throw new Exception($"Embedding blob size mismatch. bytes={blob.Length}, expected={dim * 4}.");

            var arr = new float[dim];
            Buffer.BlockCopy(blob, 0, arr, 0, blob.Length);
            return arr;
        }

        private static float L2Norm(float[] v)
        {
            double sum = 0;
            for (int i = 0; i < v.Length; i++)
                sum += (double)v[i] * v[i];
            return (float)Math.Sqrt(sum);
        }

        private static float Dot(float[] a, float[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
                sum += (double)a[i] * b[i];
            return (float)sum;
        }

        private static float CosineSim(float[] q, float qNorm, float[] d, float dNorm)
        {
            if (dNorm <= 0f) dNorm = 1e-8f;
            float dot = Dot(q, d);
            return dot / (qNorm * dNorm);
        }


    }
}
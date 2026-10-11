namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// What is done with speaker embeddings once they exist: cosine similarity, the reference average and the pairwise matrix.
/// Has no dependencies, so the tests compile this file too.
/// </summary>
internal static class VoiceMath
{
    /// <summary>The cosine of the angle between two embeddings, -1 to 1 (0 when either has no length).</summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("the embeddings differ in length");
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>
    /// The average of several embeddings, each scaled to length 1 first so a loud or long clip doesn't count for more than
    /// another. This is what a candidate is compared with when there are several references.
    /// </summary>
    public static float[] Average(IReadOnlyList<float[]> embeddings)
    {
        if (embeddings.Count == 0) throw new ArgumentException("no embeddings to average");
        var mean = new double[embeddings[0].Length];
        foreach (var e in embeddings)
        {
            double norm = 0;
            foreach (var v in e) norm += (double)v * v;
            norm = Math.Sqrt(norm);
            if (norm == 0) continue;
            for (var i = 0; i < mean.Length; i++) mean[i] += e[i] / norm;
        }
        return mean.Select(v => (float)(v / embeddings.Count)).ToArray();
    }

    /// <summary>The similarity of every pair, in the order given: [i, j] is file i against file j (1 on the diagonal).</summary>
    public static double[][] Matrix(IReadOnlyList<float[]> embeddings)
    {
        var matrix = new double[embeddings.Count][];
        for (var i = 0; i < matrix.Length; i++) matrix[i] = new double[embeddings.Count];
        for (var i = 0; i < matrix.Length; i++)
        {
            matrix[i][i] = 1;
            for (var j = i + 1; j < matrix.Length; j++)
                matrix[i][j] = matrix[j][i] = Cosine(embeddings[i], embeddings[j]);
        }
        return matrix;
    }
}

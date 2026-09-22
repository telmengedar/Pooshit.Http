using System.Collections.Generic;

namespace Http.Tests.TestSupport;

/// <summary>
/// document element which records how many bytes had already reached a sink every time it was serialized
/// </summary>
public class ReadProgressDto {
    readonly WriteRecordingSink sink;
    readonly List<long> reads;
    string text;

    /// <summary>
    /// creates a new <see cref="ReadProgressDto"/>
    /// </summary>
    /// <param name="sink">sink the document is written to</param>
    /// <param name="reads">list every read of <see cref="Value"/> appends the sink progress to</param>
    /// <param name="text">value the element carries</param>
    public ReadProgressDto(WriteRecordingSink sink, List<long> reads, string text) {
        this.sink = sink;
        this.reads = reads;
        this.text = text;
    }

    /// <summary>
    /// value the element carries, settable because the serializer only emits settable properties
    /// </summary>
    public string Value {
        get {
            reads.Add(sink.BytesWritten);
            return text;
        }
        set => text = value;
    }
}

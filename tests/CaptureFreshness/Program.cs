using System;
using DalamudAgentBridge;

var stamp = new CaptureFreshnessStamp(unchecked((int)0xA53C80F1), 7, 12, 13);
var pixels = new int[stamp.X + stamp.Width, stamp.Y + stamp.Height];
for (var i = 0; i < CaptureFreshnessStamp.Cells; i++)
    for (var dx = 0; dx < CaptureFreshnessStamp.CellSize; dx++)
        for (var dy = 0; dy < CaptureFreshnessStamp.CellSize; dy++)
            pixels[stamp.CellX(i) + dx, stamp.CellY(i) + dy] = stamp.White(i) ? 221 : 31;
Check(stamp, StampReading.Match);
Check(stamp with { Nonce = 7 }, StampReading.Different);
pixels[stamp.X + 4 * CaptureFreshnessStamp.CellSize + 1, stamp.Y + 1] = 128;
Check(stamp, StampReading.Unreadable);
Array.Clear(pixels);
Check(stamp, StampReading.Absent);
Console.WriteLine("PASS: matching, older, unreadable and absent stamps");

void Check(CaptureFreshnessStamp expected, StampReading kind)
{
    var result = expected.Decode((x, y) => pixels[x, y], out var observed);
    if (result != kind || ((kind is StampReading.Match or StampReading.Different) && observed != stamp.Nonce))
        throw new Exception($"Expected {kind}; decoded {result}, nonce {observed:X8}.");
}

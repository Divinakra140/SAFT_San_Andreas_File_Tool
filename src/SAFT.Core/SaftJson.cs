using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAFT.Core;

/// <summary>
/// How SAFT reads and writes its two record files, WITHOUT the reflection-based serializer.
///
/// This exists because of a number measured on a real device. Reading the additions record — a four
/// kilobyte file — took 600 to 900 milliseconds the FIRST time in a process, and 1 to 30 milliseconds
/// every time after. Across every log we have, that pattern holds without exception. The file is not
/// the cost; the cost is System.Text.Json waking up, working out how to serialize these classes by
/// reflection, and GENERATING CODE at runtime to do it.
///
/// SAFT is a 32-bit process running through box64, translating x86 as it goes. Runtime code
/// generation is the least friendly thing that can be asked of that arrangement, and four consecutive
/// crashes on freshly rebooted devices all landed inside that same 800 millisecond window — every one
/// of them a session that went straight to an install, so this was its first use of JSON. Sessions
/// that happened to uninstall first paid the cost earlier and showed 2 milliseconds here instead.
///
/// A source generator does that work at COMPILE time. The metadata is written into the assembly
/// rather than derived at runtime, so nothing is reflected over and no code is emitted. It is the
/// approach .NET recommends for exactly this shape of application — single file, self-contained,
/// no runtime code generation — and it should turn that 800 millisecond spike into approximately
/// nothing.
///
/// Whether that stops the crashes is not yet known. What is known is that it removes the most
/// expensive non-file operation in the app from the exact spot four crashes in a row landed on.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AdditionsManifest))]
[JsonSerializable(typeof(SaftManifest))]
internal sealed partial class SaftJsonContext : JsonSerializerContext
{
}

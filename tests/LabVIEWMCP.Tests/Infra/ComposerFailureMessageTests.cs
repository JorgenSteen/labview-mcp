// =============================================================================
// ComposerFailureMessageTests.cs - what a failed composer or reader run answers:
// the Python exception's own line as the message, and a reader failure named as one.
// =============================================================================
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public sealed class ComposerFailureMessageTests
{
    /// <summary>
    /// The reader's refusal of a VI whose type list it could not read (a field project's 3.5 MB
    /// main VI, 2026-11-13), as Python 3.14 prints it: the multi-line `raise` is abbreviated to
    /// `...<2 lines>...`, and the last four lines taken blind began mid-statement.
    /// </summary>
    private const string ReaderTraceback = """
        Traceback (most recent call last):
          File "C:\bin\scripts\aixml-pylabview\vi2aixml.py", line 2210, in <module>
            main()
            ~~~~^^
          File "C:\bin\scripts\aixml-pylabview\lvheap.py", line 172, in read_types
            raise ValueError(
            ...<2 lines>...
                "it failed on; on grpc NI's converter reads it.")
        ValueError: pylabview could not read this VI's type list (its VCTP block was kept raw), so it cannot be read as AIXML. Read the warnings of pylv_extract on the VI for the type it failed on; on grpc NI's converter reads it.
        """;

    [Fact]
    public void A_traceback_answers_its_exception_line_only()
    {
        var message = AixmlComposer.Tail(ReaderTraceback, "");

        Assert.StartsWith("ValueError: pylabview could not read this VI's type list", message);
        Assert.DoesNotContain("raise", message);
        Assert.DoesNotContain("<2 lines>", message);
    }

    [Fact]
    public void Continuation_lines_after_the_exception_line_are_kept()
    {
        var message = AixmlComposer.Tail("Traceback (most recent call last):\n  File \"x.py\"\nKeyError: 'a'\nnote: second line", "");

        Assert.Equal("KeyError: 'a' | note: second line", message);
    }

    [Fact]
    public void Output_that_is_no_traceback_keeps_its_last_four_lines()
    {
        var message = AixmlComposer.Tail("one\ntwo\nthree\nfour\nfive", "");

        Assert.Equal("two | three | four | five", message);
    }

    [Fact]
    public void A_reader_failure_is_named_as_one_and_a_composer_failure_as_before()
    {
        Assert.Equal("readerFailed", new AixmlComposer.Result(false, false, "m", [], 0, 1, Reader: true).Kind);
        Assert.Equal("composerFailed", new AixmlComposer.Result(false, false, "m", [], 0, 1).Kind);
        Assert.Equal("composerRefused", new AixmlComposer.Result(false, true, "m", [], 0, 2).Kind);
        Assert.Equal("", new AixmlComposer.Result(true, false, "No Error", [], 0, 0, Reader: true).Kind);
    }
}

using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class RequirementsAssessmentParserTests
{
    [Fact]
    public void Parses_ready_verdict_with_bounded_assumptions()
    {
        var result = RequirementsAssessmentParser.Parse("""
            ```wrighty-readiness
            {
              "schemaVersion": 1,
              "verdict": "ready",
              "reason": "The outcome and verification are explicit.",
              "blockingQuestions": [],
              "assumptions": ["Follow the adjacent naming convention."]
            }
            ```
            """);

        Assert.True(result.IsValid);
        Assert.Equal(RequirementsReadiness.Ready, result.Verdict!.Verdict);
        Assert.Empty(result.Verdict.BlockingQuestions);
        Assert.Single(result.Verdict.Assumptions);
    }

    [Fact]
    public void Parses_needs_clarification_only_with_a_blocking_question()
    {
        var result = RequirementsAssessmentParser.Parse("""
            ```wrighty-readiness
            {
              "schemaVersion": 1,
              "verdict": "needs-clarification",
              "reason": "Two incompatible retention outcomes are requested.",
              "blockingQuestions": ["Should records be retained or deleted?"],
              "assumptions": []
            }
            ```
            """);

        Assert.True(result.IsValid);
        Assert.Equal(RequirementsReadiness.NeedsClarification, result.Verdict!.Verdict);
        Assert.Single(result.Verdict.BlockingQuestions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain prose")]
    [InlineData("```wrighty-readiness\nnot json\n```")]
    [InlineData("```wrighty-readiness\n{\"schemaVersion\":2}\n```")]
    [InlineData("""
        ```wrighty-readiness
        {"schemaVersion":1,"verdict":"ready","reason":"ok","blockingQuestions":["why?"],"assumptions":[]}
        ```
        """)]
    [InlineData("""
        ```wrighty-readiness
        {"schemaVersion":1,"verdict":"needs-clarification","reason":"blocked","blockingQuestions":[],"assumptions":[]}
        ```
        """)]
    public void Fails_closed_for_missing_malformed_or_inconsistent_output(string response)
    {
        var result = RequirementsAssessmentParser.Parse(response);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Error!);
    }

    [Fact]
    public void Rejects_multiple_verdict_blocks()
    {
        const string block = """
            ```wrighty-readiness
            {"schemaVersion":1,"verdict":"ready","reason":"ok","blockingQuestions":[],"assumptions":[]}
            ```
            """;

        var result = RequirementsAssessmentParser.Parse(block + block);

        Assert.False(result.IsValid);
        Assert.Contains("more than one", result.Error);
    }

    [Theory]
    [InlineData("prefix\n```wrighty-readiness\n{\"schemaVersion\":1,\"verdict\":\"ready\",\"reason\":\"ok\",\"blockingQuestions\":[],\"assumptions\":[]}\n```")]
    [InlineData("```wrighty-readiness\n{\"schemaVersion\":1,\"verdict\":\"ready\",\"reason\":\"ok\",\"blockingQuestions\":[],\"assumptions\":[]}\n```\nsuffix")]
    [InlineData("```wrighty-readiness\n{\"schemaVersion\":1,\"schemaVersion\":1,\"verdict\":\"ready\",\"reason\":\"ok\",\"blockingQuestions\":[],\"assumptions\":[]}\n```")]
    [InlineData("```wrighty-readiness\n{\"schemaVersion\":1,\"verdict\":\"ready\",\"reason\":\"ok\",\"blockingQuestions\":[],\"assumptions\":[],\"extra\":true}\n```")]
    public void Rejects_surrounding_text_and_non_exact_schema(string response)
    {
        var result = RequirementsAssessmentParser.Parse(response);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Error!);
    }

    [Fact]
    public void Rejects_an_oversized_response_before_parsing()
    {
        var result = RequirementsAssessmentParser.Parse(
            new string('x', RequirementsAssessmentParser.MaxResponseCharacters + 1));

        Assert.False(result.IsValid);
        Assert.Contains("size", result.Error);
    }
}

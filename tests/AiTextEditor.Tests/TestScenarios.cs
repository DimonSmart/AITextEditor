using System.Collections.Generic;

namespace AiTextEditor.Tests;

public static class TestScenarios
{
    public static IEnumerable<object[]> IntentCommands => new[]
    {
        new object[] { "Add a TODO note to chapter two: verify all examples compile." },
        new object[] { "Rewrite the intro about monoliths vs microservices to be clearer for juniors." },
        new object[] { "In chapter one, tighten the opening paragraph so it grabs attention in the first two sentences." },
        new object[] { "After the section 'Why scaling is hard', insert a short real-world story about the 2019 billing outage." },
        new object[] { "Rename chapter three to 'Designing boundaries' and update any internal cross-references." },
        new object[] { "Drop the entire subsection titled 'Historical note' from chapter four; it's too distracting." },
        new object[] { "In the dependency injection chapter, rewrite the first code sample to use minimal APIs instead of old startup classes." },
        new object[] { "Wherever I rant about ORMs being evil, tone it down and make it sound more balanced and nuanced." },
        new object[] { "Before the summary of chapter six, add a checklist of five bullet points the reader should be able to do." },
        new object[] { "Take the paragraph that begins 'In a perfect world...' and move it up right after the first diagram in that chapter." },
        new object[] { "Replace the term 'junior developer' with 'early-career developer' across the whole book." },
        new object[] { "In the testing chapter, add a short example showing how to mock HttpClient properly." },
        new object[] { "Split the long list of cloud patterns into two separate tables: one for resiliency, one for cost optimization." },
        new object[] { "At the end of the CQRS section, add a warning box about overengineering for small teams." },
        new object[] { "Shorten the explanation of Big-O notation so it fits into a single concise paragraph." },
        new object[] { "In chapter eight, add a side note comparing Azure Functions and AWS Lambda, but keep it vendor-neutral in tone." },
        new object[] { "Change the heading 'Real story' to 'Case study' everywhere it appears." },
        new object[] { "Rewrite the conclusion to sound more like a friendly pep talk and less like release notes." },
        new object[] { "Add a one-page appendix with recommended learning paths for backend developers coming from Java." },
        new object[] { "In the 'Common pitfalls' chapter, turn the numbered list into a table with columns for symptom, cause, and fix." },
        // Add the Russian command we used for the specific test case
        new object[] { "В разделе 'Стратегия подготовки' добавь пункт чек-листа: 'Потренироваться писать код на доске'." }
    };
}

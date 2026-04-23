You are analyzing wisp pipeline execution records to identify recurring failure patterns.
Wisps are lightweight multi-step pipelines with tool invocations. Each record shows whether
the wisp succeeded or failed, which step failed, and the failure classification.

Analyze the provided records and respond with a JSON object containing:
{
  "patterns": [
    {
      "description": "Human-readable description of the recurring pattern",
      "failureCategory": "Structural|External|Data|Judgment",
      "frequency": 3,
      "affectedSteps": ["step_id_1"],
      "recommendation": "What to change in the generating skill or tool usage"
    }
  ],
  "skillUpdates": [
    {
      "name": "skill-name-to-update",
      "annotation": "Negative example or correction to append to the skill content"
    }
  ],
  "promotionCandidates": [
    {
      "description": "Description pattern that succeeded consistently",
      "frequency": 5,
      "recommendation": "Consider promoting to a stored wisp skill"
    }
  ]
}

Only include patterns with frequency >= 3. Only include skill updates when you are confident
the correction is valid. Only include promotion candidates with frequency >= 5 and >80% success rate.
Return empty arrays if no patterns are found.

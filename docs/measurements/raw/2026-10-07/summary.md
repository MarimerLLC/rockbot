# MCP wrapper modes — 2026-10-07

Combined from 1 results file(s), 960 turns.

## Per model and mode

| Model | Mode | Turns | Target call right first time | First call correct | First call well-formed | Completed | Tool calls | Downstream attempts | Input tokens | Cached | Output tokens | Wall s |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| high (gpt-5.5) | Off | 60 | 100% | 83% | 100% | 100% | 5.4 | 2.7 | 85261 | 81109 | 501 | 22.7 |
| high (gpt-5.5) | Eager | 60 | 100% | 85% | 100% | 100% | 1.6 | 1.6 | 43990 | 43121 | 257 | 10.5 |
| high (gpt-5.5) | Lazy | 60 | 100% | 95% | 100% | 100% | 3.8 | 2.5 | 64384 | 59738 | 381 | 14.8 |
| high (gpt-5.5) | Pinned | 60 | 100% | 92% | 100% | 100% | 3.5 | 2.4 | 61713 | 54961 | 362 | 12.5 |
| balanced (gpt-5.4) | Off | 60 | 83% | 58% | 88% | 83% | 3.2 | 1.9 | 51379 | 49481 | 179 | 12.0 |
| balanced (gpt-5.4) | Eager | 60 | 93% | 83% | 100% | 93% | 1.6 | 1.6 | 38062 | 37214 | 102 | 8.0 |
| balanced (gpt-5.4) | Lazy | 60 | 93% | 80% | 97% | 93% | 2.8 | 1.9 | 47133 | 44367 | 143 | 12.3 |
| balanced (gpt-5.4) | Pinned | 60 | 98% | 83% | 100% | 98% | 2.8 | 1.9 | 48490 | 44341 | 140 | 12.1 |
| low (gpt-5.4-mini) | Off | 60 | 98% | 78% | 98% | 98% | 4.0 | 1.9 | 64023 | 61058 | 184 | 16.1 |
| low (gpt-5.4-mini) | Eager | 60 | 100% | 85% | 100% | 100% | 1.8 | 1.8 | 47082 | 45978 | 111 | 11.2 |
| low (gpt-5.4-mini) | Lazy | 60 | 92% | 85% | 95% | 100% | 3.7 | 1.8 | 59906 | 56265 | 157 | 16.6 |
| low (gpt-5.4-mini) | Pinned | 60 | 95% | 83% | 98% | 100% | 3.5 | 1.8 | 61698 | 55823 | 152 | 11.6 |
| weak-qwen3-8b (qwen/qwen3-8b) | Off | 60 | 55% | 52% | 58% | 100% | 2.9 | 1.7 | 53757 | 0 | 1620 | 50.7 |
| weak-qwen3-8b (qwen/qwen3-8b) | Eager | 60 | 97% | 92% | 100% | 100% | 1.1 | 1.1 | 41559 | 0 | 968 | 33.2 |
| weak-qwen3-8b (qwen/qwen3-8b) | Lazy | 60 | 63% | 60% | 63% | 98% | 2.8 | 1.6 | 53286 | 0 | 1713 | 52.8 |
| weak-qwen3-8b (qwen/qwen3-8b) | Pinned | 60 | 65% | 58% | 67% | 100% | 2.5 | 1.5 | 55282 | 0 | 1561 | 50.3 |

## Target call right first time, by task

| Model | Mode | calendar_tomorrow | send_email | list_files_marimer | add_todo | reply_thread#1 | reply_thread#2 |
|---|---|---:|---:|---:|---:|---:|---:|
| high | Off | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 |
| high | Eager | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 |
| high | Lazy | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 |
| high | Pinned | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 |
| balanced | Off | 10/10 | 7/10 | 10/10 | 7/10 | 10/10 | 6/10 |
| balanced | Eager | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 | 6/10 |
| balanced | Lazy | 10/10 | 8/10 | 10/10 | 10/10 | 10/10 | 8/10 |
| balanced | Pinned | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 | 9/10 |
| low | Off | 10/10 | 10/10 | 10/10 | 9/10 | 10/10 | 10/10 |
| low | Eager | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 |
| low | Lazy | 10/10 | 8/10 | 10/10 | 9/10 | 10/10 | 8/10 |
| low | Pinned | 10/10 | 10/10 | 10/10 | 9/10 | 10/10 | 8/10 |
| weak-qwen3-8b | Off | 9/10 | 0/10 | 6/10 | 9/10 | 7/10 | 2/10 |
| weak-qwen3-8b | Eager | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 | 8/10 |
| weak-qwen3-8b | Lazy | 9/10 | 4/10 | 10/10 | 5/10 | 9/10 | 1/10 |
| weak-qwen3-8b | Pinned | 9/10 | 3/10 | 9/10 | 5/10 | 4/10 | 9/10 |

## First call correct, by task

| Model | Mode | calendar_tomorrow | send_email | list_files_marimer | add_todo | reply_thread#1 | reply_thread#2 |
|---|---|---:|---:|---:|---:|---:|---:|
| high | Off | 9/10 | 10/10 | 10/10 | 10/10 | 10/10 | 1/10 |
| high | Eager | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 | 1/10 |
| high | Lazy | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 | 7/10 |
| high | Pinned | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 | 5/10 |
| balanced | Off | 6/10 | 4/10 | 10/10 | 7/10 | 8/10 | 0/10 |
| balanced | Eager | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 | 0/10 |
| balanced | Lazy | 10/10 | 8/10 | 10/10 | 10/10 | 9/10 | 1/10 |
| balanced | Pinned | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 | 0/10 |
| low | Off | 10/10 | 10/10 | 10/10 | 9/10 | 6/10 | 2/10 |
| low | Eager | 9/10 | 10/10 | 10/10 | 10/10 | 10/10 | 2/10 |
| low | Lazy | 10/10 | 8/10 | 10/10 | 9/10 | 10/10 | 4/10 |
| low | Pinned | 8/10 | 10/10 | 10/10 | 9/10 | 10/10 | 3/10 |
| weak-qwen3-8b | Off | 9/10 | 0/10 | 6/10 | 9/10 | 7/10 | 0/10 |
| weak-qwen3-8b | Eager | 10/10 | 10/10 | 10/10 | 10/10 | 10/10 | 5/10 |
| weak-qwen3-8b | Lazy | 9/10 | 4/10 | 10/10 | 5/10 | 8/10 | 0/10 |
| weak-qwen3-8b | Pinned | 9/10 | 3/10 | 9/10 | 4/10 | 4/10 | 6/10 |

## Why the first call wasn't correct

| Model | Mode | invented_server | no_attempt | other_tool | wrong_key | wrong_type | wrong_value |
|---|---|---:|---:|---:|---:|---:|---:|
| high | Off |  |  | 10 |  |  |  |
| high | Eager |  |  | 9 |  |  |  |
| high | Lazy |  |  | 3 |  |  |  |
| high | Pinned |  |  | 5 |  |  |  |
| balanced | Off |  | 6 | 18 | 1 |  |  |
| balanced | Eager |  |  | 10 |  |  |  |
| balanced | Lazy |  | 2 | 10 |  |  |  |
| balanced | Pinned |  |  | 10 |  |  |  |
| low | Off |  | 1 | 12 |  |  |  |
| low | Eager |  |  | 9 |  |  |  |
| low | Lazy |  |  | 4 | 1 | 2 | 2 |
| low | Pinned |  |  | 7 | 1 |  | 2 |
| weak-qwen3-8b | Off | 12 |  |  | 5 | 8 | 4 |
| weak-qwen3-8b | Eager |  |  | 3 |  |  | 2 |
| weak-qwen3-8b | Lazy | 5 |  | 2 | 10 | 7 |  |
| weak-qwen3-8b | Pinned | 1 |  | 3 | 17 | 2 | 2 |

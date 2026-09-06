using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Tools.FileSystem;

/// <summary>
/// Registers the shared-volume file tools. <c>analyze_file</c> is conditional — see
/// <see cref="TryRegisterAnalyzeFile"/>; the rest are always registered.
/// </summary>
/// <param name="services">
/// Used to resolve <see cref="ILlmClient"/> and <see cref="LlmTierOptions"/>, which this
/// package treats as optional: it is consumed by hosts that configure no LLM client at all,
/// and their absence simply means <c>analyze_file</c> is not offered.
/// </param>
internal sealed class FileSystemToolRegistrar(
    IToolRegistry registry,
    FileSystemOptions options,
    IServiceProvider services,
    ILogger<FileSystemToolRegistrar> logger) : IHostedService
{
    private const string WriteSchema = """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Relative path within the shared volume (e.g. 'drafts/report.md')"
            },
            "content": {
              "type": "string",
              "description": "UTF-8 text content to write"
            }
          },
          "required": ["path", "content"]
        }
        """;

    private const string ReadSchema = """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Relative path within the shared volume"
            }
          },
          "required": ["path"]
        }
        """;

    private const string EditSchema = """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Relative path within the shared volume (e.g. 'canon/NPCs.md')"
            },
            "old_string": {
              "type": "string",
              "description": "Exact text to replace, copied verbatim from the file including whitespace and indentation. Must match exactly once unless replace_all is true."
            },
            "new_string": {
              "type": "string",
              "description": "Text to replace it with. Use an empty string to delete the matched text."
            },
            "replace_all": {
              "type": "boolean",
              "description": "Replace every occurrence instead of requiring a unique match. Defaults to false."
            }
          },
          "required": ["path", "old_string", "new_string"]
        }
        """;

    private const string ListSchema = """
        {
          "type": "object",
          "properties": {
            "prefix": {
              "type": "string",
              "description": "Optional directory prefix to filter results (e.g. 'drafts/')"
            }
          }
        }
        """;

    private const string DeleteSchema = """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Relative path of the file to delete"
            }
          },
          "required": ["path"]
        }
        """;

    private const string GetPathSchema = """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Relative path within the shared volume"
            }
          },
          "required": ["path"]
        }
        """;

    private const string AnalyzeSchema = """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Relative path of the image within the shared volume (e.g. 'attachments/diagram.png')"
            },
            "prompt": {
              "type": "string",
              "description": "What you need to know about the file. Be specific — you get back only the answer to this question, and asking again costs another look."
            },
            "tier": {
              "type": "string",
              "enum": ["low", "balanced", "high"],
              "description": "Which model tier examines the file. Defaults to balanced; use high for dense diagrams or fine detail."
            }
          },
          "required": ["path", "prompt"]
        }
        """;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Register(new ToolRegistration
        {
            Name = "file_write",
            Description = "Write UTF-8 text content to a file on the shared volume (e.g. 'drafts/report.md'). Creates parent directories as needed.",
            ParametersSchema = WriteSchema,
            Source = "filesystem"
        }, new FileWriteToolExecutor(options));
        logger.LogInformation("Registered file tool: file_write");

        registry.Register(new ToolRegistration
        {
            Name = "file_edit",
            Description = """
                Replace an exact piece of text in an existing file on the shared volume,
                leaving the rest of the file untouched. Prefer this over file_write when
                changing part of a file — file_write replaces the entire file, so anything
                not reproduced in full is lost. old_string must match exactly once unless
                replace_all is set.
                """,
            ParametersSchema = EditSchema,
            Source = "filesystem"
        }, new FileEditToolExecutor(options));
        logger.LogInformation("Registered file tool: file_edit");

        registry.Register(new ToolRegistration
        {
            Name = "file_read",
            Description = "Read the UTF-8 text content of a file on the shared volume.",
            ParametersSchema = ReadSchema,
            Source = "filesystem"
        }, new FileReadToolExecutor(options));
        logger.LogInformation("Registered file tool: file_read");

        registry.Register(new ToolRegistration
        {
            Name = "file_list",
            Description = "List files on the shared volume as a JSON array of relative paths. Optional prefix filters results (e.g. 'drafts/').",
            ParametersSchema = ListSchema,
            Source = "filesystem"
        }, new FileListToolExecutor(options));
        logger.LogInformation("Registered file tool: file_list");

        registry.Register(new ToolRegistration
        {
            Name = "file_delete",
            Description = "Delete a file from the shared volume.",
            ParametersSchema = DeleteSchema,
            Source = "filesystem"
        }, new FileDeleteToolExecutor(options));
        logger.LogInformation("Registered file tool: file_delete");

        registry.Register(new ToolRegistration
        {
            Name = "file_get_path",
            Description = """
                Returns the absolute local filesystem path for a file on the shared volume.
                Use this when another tool requires a local file path rather than content
                (e.g. uploading a file to OneDrive or attaching to an email).
                """,
            ParametersSchema = GetPathSchema,
            Source = "filesystem"
        }, new FileGetPathToolExecutor(options));
        logger.LogInformation("Registered file tool: file_get_path");

        TryRegisterAnalyzeFile();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Registers <c>analyze_file</c> only when some tier declares it accepts image input.
    /// Offering the tool otherwise teaches the model a capability the deployment does not have,
    /// which it then spends turns trying to use.
    /// </summary>
    private void TryRegisterAnalyzeFile()
    {
        var tierOptions = services.GetService<LlmTierOptions>();
        var llmClient = services.GetService<ILlmClient>();

        if (tierOptions is null || llmClient is null)
        {
            logger.LogInformation(
                "File tool analyze_file not registered: no {Missing} configured",
                tierOptions is null ? nameof(LlmTierOptions) : nameof(ILlmClient));
            return;
        }

        var visionTiers = VisionTiers.From(tierOptions);

        if (visionTiers.Length == 0)
        {
            logger.LogInformation(
                "File tool analyze_file not registered: no LLM tier sets SupportsImageInput");
            return;
        }

        registry.Register(new ToolRegistration
        {
            Name = "analyze_file",
            Description = """
                Look at an image on the shared volume and answer a question about it. Use this
                for anything you cannot read as text — diagrams, screenshots, charts, scans,
                photos. The file is shown to a vision-capable model as an actual image; you get
                back that model's answer to your prompt, not the file's bytes. Never try to read
                an image with file_read: it returns unusable text and floods your context.
                """,
            ParametersSchema = AnalyzeSchema,
            Source = "filesystem"
        }, new AnalyzeFileToolExecutor(options, llmClient, visionTiers, logger));

        logger.LogInformation(
            "Registered file tool: analyze_file (vision tiers: {Tiers})",
            string.Join(", ", visionTiers));
    }
}

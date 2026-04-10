using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.FileSystem;

internal sealed class FileSystemToolRegistrar(
    IToolRegistry registry,
    FileSystemOptions options,
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

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

// Tool definitions for the bridge (chat/completions format).
public static class ToolDefinitions
{
    public static JsonArray GetTools()
    {
        var tools = new JsonArray();

        // shell tool - execute shell commands
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "shell",
                ["description"] = "Execute a shell command in the workspace. Returns stdout/stderr. Use for ls, cat, grep, find, git, dotnet, python, etc.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["command"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The shell command to execute (e.g., 'ls -la', 'cat file.txt', 'grep pattern file')"
                        }
                    },
                    ["required"] = new JsonArray("command")
                }
            }
        });

        // read tool - read file contents
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "read",
                ["description"] = "Read the contents of a file. Supports text files, images, and PDFs.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Path to the file to read"
                        },
                        ["offset"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Line or byte offset to start reading from (1-based)",
                            ["default"] = 0
                        },
                        ["limit"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Maximum number of lines or bytes to read",
                            ["default"] = 2000
                        }
                    },
                    ["required"] = new JsonArray("path")
                }
            }
        });

        // write tool - write file contents
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "write",
                ["description"] = "Write content to a file, overwriting if it exists. Creates parent directories automatically.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Path to the file to write"
                        },
                        ["content"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Content to write to the file"
                        }
                    },
                    ["required"] = new JsonArray("path", "content")
                }
            }
        });

        // edit tool - edit file contents
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "edit",
                ["description"] = "Edit a file by finding and replacing exact text. Preserves indentation and formatting.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Path to the file to edit"
                        },
                        ["oldString"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Exact text to find and replace (must be unique)"
                        },
                        ["newString"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Text to replace oldString with"
                        },
                        ["replaceAll"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Replace all occurrences (default: false)",
                            ["default"] = false
                        }
                    },
                    ["required"] = new JsonArray("path", "oldString", "newString")
                }
            }
        });

        // grep tool - search file contents
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "grep",
                ["description"] = "Search file contents using ripgrep regex syntax. Returns matching file paths, line numbers, and previews.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["pattern"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Regular expression or literal text to match"
                        },
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Directory to search (default: workspace root)"
                        },
                        ["include"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Glob pattern to filter files (e.g., '*.cs', '*.{ts,tsx}')"
                        }
                    },
                    ["required"] = new JsonArray("pattern")
                }
            }
        });

        // glob tool - search file paths
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "glob",
                ["description"] = "Search file paths using glob patterns.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["pattern"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Glob pattern to match files against (e.g., '**/*.cs')"
                        },
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Directory to search (default: workspace root)"
                        }
                    },
                    ["required"] = new JsonArray("pattern")
                }
            }
        });

        // task tool - REMOVIDO: sem executor implementado, não anunciar ao modelo.
        // (Reintroduzir aqui + handler dedicado quando houver subagentes.)

        return tools;
    }
}
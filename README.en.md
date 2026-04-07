# Harvnyx
[README for CN](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/blob/master/README.md) [README for EN](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/blob/master/README.en.md) [README for RU](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/blob/master/README.ru.md)
> **Note:** This list focuses on stability, resource management, logic flaws, and maintainability issues. **Security-related items (e.g., plaintext passwords, plugin validation) are not included for now.** Community developers are requested to address them in order of priority.

> **Warning:** Do not run this program on your primary computer, as it currently does not guarantee that information will not be leaked!
---
## Core Positioning
Harvnyx is more than just a simple command interpreter; it is an integrated command execution framework. It seamlessly blends internal commands, external Windows executables, and plugin-extended commands, providing users with a unified interaction entry point.

## Technical Architecture and Core Modules
The program consists of several core classes that work together:
1. **Program.cs:** The program's interaction logic and main loop.
   - Main loop: continuously reads, parses (supports connecting multiple commands with `&&`, `-and`, `-or` operators), and executes user input.
   - Advanced interaction:
      - Tab auto-completion: supports intelligent and cyclic completion of commands, subcommands, options, and parameter values.
      - Syntax highlighting: color‑codes different elements (main commands, parameters, values, etc.) as you type.
      - History and suggestions: records command history and provides input suggestions.
   - Dynamic prompt: configurable to display the current working directory or network (VPN/IP) information.
2. **CommandProcessor.cs:** The command control segment of the system.
   - Command routing: parses user input and decides the execution path based on the command name: internal command, plugin command, or external system command.
   - Conflict resolution: provides four built‑in handling modes (`Harvnyx`/`Windows`/`Mix`/`True`) to intelligently or interactively resolve name conflicts between internal commands and external programs.
   - Argument parsing: supports complex arguments with quotes and escape characters.
3. **ConfigProcessor.cs:** Unified configuration and state manager.
   - Loads and saves user settings from the `config.ini` file.
   - **Key configurations:** command inquiry mode (`CommandInquiry`), prompt display content (`additional`), clear screen on startup (`ShellClear`).
   - **System monitoring:** starts a background task that periodically caches and quickly provides CPU usage/frequency and memory usage information.
4. **CommandCompleter (`CommandProcessor.cs`):** Completion and suggestion engine.
   - Maintains a list of all available commands (built‑in, plugin, external) along with metadata for their subcommands and options.
   - Dynamically computes the currently active command list based on settings in `ConfigProcessor`, providing data for completion and highlighting in `Program`.
   - ~~AI ghostwriting is awesome~~
5. **CommandExecute (`CommandProcessor.cs`):** Command executor.
   - **`sudo`:** Simulates Unix‑like privilege elevation on Windows. Supports running commands as another user, background execution, timestamp caching, etc.
   - **`shell`:** Manages the shell’s own behavior, such as configuring the prompt display mode and command inquiry mode.
   - **`tasks`:** Manages background/foreground tasks started by Harvnyx – list, view details, or terminate tasks.
6. **Plugin System (`ICommand` interface & `CommandManager`) (`CommandProcessor.cs`, `Program.cs`):** Core of extensibility.
   - Defines the `ICommand` interface; any class implementing this interface can be loaded as a plugin command.
   - `CommandManager` dynamically scans and loads `.dll` files in the current directory, automatically registers the commands they contain, and updates the completion system.
7. **TaskManager (`Program.cs`):** Task hosting service. Tracks processes and asynchronous tasks started by Harvnyx, assigns them IDs, and allows users to manage and monitor them via the `tasks` command.

## [Security Notes](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/blob/master/QuestionList)
[Security notes for 0.1.80-alpha.2](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/blob/master/QuestionList/0.1.80-alpha.2.md)
## Installation Guide
### System Requirements
- **Operating System:** Windows 7 SP1 / Windows 10 / Windows 11 (64‑bit recommended)
- **Runtime:** [.NET 8.0 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) or higher
- **Dependencies:** None

### Installation Methods

#### Method 1: Install via package
1. Go to the [Releases](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/blob/master/Program) page and download the latest `Harvnyx.zip`
2. Extract the archive to any directory (e.g., `C:\Harvnyx`)
3. Enter the extracted folder and double‑click `Harvnyx.exe` to run

#### Method 2: Compile from source
1. Prerequisites
   - [.NET 8.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
   - Git (optional, for cloning the repository)
2. Compilation steps
```bash
# Clone the repository
git clone https://github.com/NORTHTECH-Group/Harvnyx.git
cd Harvnyx

# Restore dependencies and build
dotnet restore
dotnet build -c Release

# Run
dotnet run --project Harvnyx.csproj
```
The compiled executable is located at `bin\Release\net8.0\Harvnyx.exe`

### First‑run configuration
- The program automatically generates a `config.ini` configuration file in its own directory.
- Command plugins (`.dll`) should be placed in the same directory as the program; they will be loaded automatically.
- The default command handling mode is `Harvnyx` (use only internal commands). You can change it with `shell -i <mode>`. (See `Core Positioning` section 2, subsection 2 for details.)
     - ~~**Foolproof tips**~~
        - **`Harvnyx`:** Use only internal commands
        - **`Windows`:** Use only Windows commands
        - **`Mix`:** Use mixed commands
        - **`True`:** Ask the user before executing a command

### Uninstallation
Simply delete the program folder. No registry entries remain.

## Contributing
Thank you for your interest in Harvnyx! We welcome any form of contribution, including but not limited to: reporting bugs, suggesting new features, improving documentation, and submitting code.
### Code of Conduct
Please abide by the [license](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/blob/master/LICENSE.txt) and remain friendly and respectful.
### How to Contribute
#### 1. Report a Bug or Suggest a Feature
- Search the [Issues](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/issues) page to see if the same problem already exists.
- If not, open a new Issue and choose the appropriate template:
  - **Bug report:** attach system environment, reproduction steps, error logs (using the output from `print`).
  - **Feature request:** describe the use case and expected behavior.
#### 2. Improve Documentation
- Documentation is located in the `docs/` directory (or this README).
- Fix typos, improve formatting, add examples – all can be submitted as PRs.
#### 3. Submit Code
##### Prerequisites
- Knowledge of C# and .NET 8.0
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) installed locally
##### Development Workflow
```bash
# Fork the main repository to your account, then clone your fork
git clone https://github.com/your-username/Harvnyx.git
cd Harvnyx

# Add the main repository as upstream (optional, for easier syncing)
git remote add upstream https://github.com/NORTHTECH-Group/Harvnyx.git

# Create a feature branch
git checkout -b feature/your-feature-name

# Make your changes and ensure the code compiles
dotnet build

# Run (optional)
dotnet run

# Commit your changes (follow the commit convention)
git add .
git commit -m "type: short description"

# Push to your fork
git push origin feature/your-feature-name
```
#### 4. Code Conventions
1. **Asynchrony:** All potentially blocking operations should use `async/await` and support `CancellationToken`.
2. **Messaging:** Use the `print` class to output confirmations/errors/warnings.
```C#
// Example
print.error("command/severity", "problem description", {error code print.ErrorCodes});
// Optional severity levels
public static class Severity
{
    public const string LOW = "Low";
    public const string MEDIUM = "Medium";
    public const string HIGH = "High";
    public const string CRITICAL = "Critical";
    public const string FATAL = "Fatal";
}
print.warning("command/severity", "problem description");
print.success("command/severity(optional)", "problem description");
```
---
## [Download](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/blob/master/Historical.md)

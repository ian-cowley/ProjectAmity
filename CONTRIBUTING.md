# Contributing to Project Amity

Thank you for your interest in contributing to Project Amity! We welcome community contributions to make this localized media streaming environment even better.

## How to Contribute

### 1. Reporting Bugs
- Open a GitHub issue describing the bug.
- Include details about your operating system, browser, media codecs, and steps to reproduce.
- Attach log files if applicable (found in `C:\Users\<username>\AppData\Roaming\ProjectAmity\Logs` or similar).

### 2. Proposing Features
- Open a feature request issue to discuss major additions before starting work.
- Explain the user benefit, layout, and implementation outline.

### 3. Submitting Pull Requests
- Fork the repository and create a branch: `feature/your-feature-name` or `bugfix/your-fix-name`.
- Place all source files in the `src/` directory.
- Follow C# styling guidelines (PascalCase for public methods, camelCase with underscore prefixes for private fields).
- Ensure JavaScript files follow ESLint best practices and styling is responsive.
- Run `dotnet build` to ensure there are no compilation errors or warnings.
- Open a Pull Request referencing the related issue.

## Development Environment Setup
1. Clone the repository:
   ```bash
   git clone https://github.com/username/ProjectAmity.git
   ```
2. Install prerequisites:
   - [.NET 10 SDK](https://dotnet.microsoft.com/download)
   - [FFmpeg and FFprobe](https://ffmpeg.org/download.html) (Ensure they are on your system `PATH`)
3. Run the project locally:
   ```bash
   dotnet run --project src/ProjectAmityServer/ProjectAmityServer.csproj
   ```
4. Access the web client in your browser at `http://localhost:5279`.

## Code of Conduct
Please be respectful and collaborative in all issues, pull requests, and discussions. We aim to foster an inclusive and welcoming environment for everyone.

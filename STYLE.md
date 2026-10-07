# AniDownloader Code Style Guide

This guide outlines the code conventions and best practices for contributing to AniDownloader. The focus is on readability, modularity, and efficient asynchronous processing.

Readable and maintainable code is preferred over clever or compact code. If a shorter form hides control flow, error paths, or data shape, write the longer form. Do not compress logic to save lines.

1. Naming Conventions

    Classes & Structs: Use PascalCase (e.g., SeriesDownloader, Program). Class names should be nouns or noun phrases that describe the entity or functionality.
    Fields & Variables:
        Use camelCase for private fields and local variables (e.g., currentlyScanningSeries, seriesUrlEncoded).
        Static fields are named in PascalCase (e.g., SeriesTableFilePath). Constants may follow PascalCase unless they are strongly conventionally suited to uppercase.
        Field names should be descriptive and specific (e.g., lastNyaaRequestTime instead of LastNyaaRequest).
    Methods: Use PascalCase for method names (e.g., StartDownloads, PrintUpdateTable). Method names should start with a verb and clearly describe the action performed.
    Properties: Use PascalCase and name according to what they represent or return (e.g., CurrentStatus).
    Prefer explicit names over abbreviations. A reader should not have to expand a name to know what it does. Prefer StartDownloads, PauseSeries, RequireValidSettings, SelectUncensoredEpisode over sd, ps, chk, or epSel. The same rule applies outside this project: completeSale, pauseTicket, and requireAdmin are better than cmpSale, pTkt, and reqAdm.
    Avoid acronyms unless they are already the domain term used in this codebase (for example HTTP, XML, MKV). If an abbreviation is kept, it must be used consistently.

2. Method Structure and Organization

    Functional Grouping: Group methods by functionality. For example, initialization methods (like LoadSeriesTable) should be grouped together and separated from processing methods (like UpdateSeriesDataTable).
    Entry Points: Use a dedicated entry point method (like Main or Start) to initialize and kick off the primary tasks.
    Task-Specific Methods: Encapsulate logic specific to tasks (e.g., StartDownloadsTask, CleanEncodedFilesTask) within dedicated methods or delegate expressions.
    Keep methods focused. Split a method when it both decides policy and performs I/O, or when a reader has to hold more than one failure path in mind at once.

3. Encapsulation and Dependency Management

    Private Fields: Use private fields to encapsulate all internal state and helper objects (e.g., downloader, SeriesTable). This protects state and reduces dependency exposure.
    Minimize Global Dependencies: If a global or static dependency is needed, encapsulate access through methods rather than directly embedding logic that relies on the global instance (e.g., Global.TaskAdmin.Logger.Log).
    Constructor Initialization: Initialize all required dependencies within the constructor. Avoid hardcoding initialization within methods if a dependency could be reused elsewhere or may need to be configured differently.

4. Asynchronous Programming

    Use async/await for asynchronous tasks wherever possible to avoid blocking the main thread (e.g., await GetWebDataFromUrl).
    Return Task types: Methods performing asynchronous operations should return Task or Task<T> to allow calling code to manage and await them if needed.
    Rate Limiting: Use rate-limiting logic to prevent overloading remote services (e.g., throttling requests in GetWebDataFromUrl).
    Do not block on async work with .Result or .Wait() on the main flow. Propagate cancellation where a task can be stopped.

5. Error Handling

    Try-Catch Blocks: Use try-catch around code prone to exceptions, such as network calls or file operations (e.g., GetWebDataFromUrl, LoadSeriesTable).
    Logging Errors: Log exceptions with sufficient context (method name and error message) using Global.TaskAdmin.Logger.EX_Log.
    Fail Gracefully: Ensure the program continues operation where possible, even after an exception. Where not possible, notify the user and terminate gracefully.
    Handle failures explicitly. Do not swallow exceptions. An empty catch, a catch that only returns a default, or a catch that logs and continues without recording the failed item is not acceptable.
    Name the failure and the next action. Typical cases in this project:
        Data load and save errors: SeriesData.xml read/write failures, DataTable schema mismatches, and corrupt or partial XML. Do not continue with an empty series table as if the load succeeded.
        Network and source errors: Nyaa.si HTTP failures, timeouts, rate-limit responses, and malformed result pages.
        Torrent errors: add, hash, stall, and client failures. Leave the episode status explicit instead of resetting it silently.
        Transcode and tool errors: ffmpeg or mkvmerge missing, non-zero exit, or unreadable output. Do not delete the source file if the transcode did not succeed.
        Invalid configuration: missing paths, out-of-range intervals, invalid bind addresses, and invalid size or language filters.
        Invalid episode data: unparseable episode numbers, empty titles, and sizes or ages outside the configured bounds.
    The same rule applies to any other explicit domain failure, including database errors, print errors, invalid credentials, and invalid quantities or prices: surface the error, log context, and either retry with a visible state or stop that operation. Do not convert it into a success or a null.
    Catch the narrowest exception type that matches the failure. Rethrow or wrap with context when the caller cannot recover.

6. Task Management and Modularity

    Task Delegates: Encapsulate each major function (e.g., StartDownloads, CleanEncodedFiles) within a delegate and register with Global.TaskAdmin.NewTask.
    Reusability: Code within tasks should be modular, allowing individual tasks to be modified or reused without affecting other parts of the code.
    Avoid Hardcoding: Avoid hardcoding configuration settings (e.g., download intervals or file paths). Use configurable settings to enhance reusability.

7. Data Management

    DataTables and XML:
        Store and manage data in DataTable objects. Use XML serialization/deserialization (LoadSeriesTable) to maintain persistence across sessions.
        Separate data loading (LoadSeriesTable) from data processing logic (UpdateSeriesDataTable) to keep concerns distinct and code maintainable.
    Column Naming and Constraints:
        Define column names in DataTable objects clearly and concisely, indicating the data type or purpose (e.g., "Episode" for episode names, "Status" for download status).
        Set primary keys and constraints on DataTable objects to ensure data integrity.
    Do not change SeriesData.xml column names or value meanings without a migration path. Existing user tables must still load.

8. Console and Feedback Management

    Real-Time Console Updates: Use PrintUpdateTable to update the console in a single, coordinated output for clear, real-time feedback on program state.
    Formatting: Align strings using helper methods (e.g., MatchStringLenghtWithSpaces) to ensure consistent formatting across console updates.
    Limit Output: Minimize extraneous output in loops or asynchronous methods to prevent console clutter.

9. Regex and Parsing

    Use of Regular Expressions: When using regular expressions (e.g., for episode number extraction), encapsulate regex patterns in constants or provide descriptive comments to clarify their purpose.
    Parsing and Validation: Validate parsed values, such as ProbableEpNumber, and handle cases where parsing fails gracefully.
    Prefer a named, commented pattern over an inline expression. If a parse fails, record why instead of substituting a guessed episode number.

10. Logging and Queueing Operations

    Operations Queue: Use Global.currentOpsQueue to manage queued status messages for real-time feedback.
    Priority Logging: Use logging selectively, ensuring high-priority actions like task start, task end, and errors are logged.
    Error logs must include the operation, the series or episode identifier when one exists, and the exception message. Do not log secrets or full credential material.

11. Code Readability

    Use Comments for Complex Logic: Add comments to explain any complex or non-obvious logic, especially in areas involving regular expressions or file operations.
    Consistent Spacing: Use consistent line spacing between methods and within methods to enhance readability.
    Limit Nesting: Aim to minimize deeply nested logic by splitting complex operations into smaller methods or using guard clauses.
    Prefer a straight-line sequence of named steps over a compact expression that mixes filtering, parsing, and I/O.

12. XML Documentation Comments

    Every public method, every public property with non-obvious behavior, and every non-trivial private method gets a concise XML comment.
    Use summary for what the method does and why a caller would use it. Keep it to one or two sentences.
    Add param and returns when the name alone does not make the contract obvious.
    Add a short "how it works" remark only when the logic is not obvious from the method name and the code. Do not restate each line.
    Trivial private getters, setters, and one-line wrappers do not need a comment.
    Example:

        /// <summary>
        /// Selects the uncensored release when both candidates match the same episode.
        /// </summary>
        /// <remarks>
        /// How it works: compares titles against Settings.UncensoredEpisodeRegex and keeps the current episode when the candidate is not a better uncensored match.
        /// </remarks>

13. Explicit Types

    Always use explicit types. Write List<OnlineEpisodeElement> episodes, not var episodes.
    Do not use var for locals, foreach variables, or LINQ query results.
    The exception is where an explicit type is impractical, such as an anonymous object created for JSON. Even then, prefer object over var when the value is stored.
    Anonymous JSON payloads should be built at the edge that serializes them, not passed through the rest of the program as var.
    Use the concrete collection type you intend to keep (List<T>, Dictionary<TKey, TValue>, DataTable) rather than an inferred result type.

By following this style guide, contributions to AniDownloader will be consistent, modular, and maintainable. The focus on asynchronous design, modular task handling, and clear logging will keep the program efficient and manageable for continuous execution in a terminal or daemon context.

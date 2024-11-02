# AniDownloader Code Style Guide

This guide outlines the code conventions and best practices for contributing to AniDownloader. The focus is on readability, modularity, and efficient asynchronous processing.
1. Naming Conventions

    Classes & Structs: Use PascalCase (e.g., SeriesDownloader, Program). Class names should be nouns or noun phrases that describe the entity or functionality.
    Fields & Variables:
        Use camelCase for private fields and local variables (e.g., currentlyScanningSeries, seriesUrlEncoded).
        Static fields are named in PascalCase (e.g., SeriesTableFilePath). Constants may follow PascalCase unless they are strongly conventionally suited to uppercase.
        Field names should be descriptive and specific (e.g., lastNyaaRequestTime instead of LastNyaaRequest).
    Methods: Use PascalCase for method names (e.g., StartDownloads, PrintUpdateTable). Method names should start with a verb and clearly describe the action performed.
    Properties: Use PascalCase and name according to what they represent or return (e.g., CurrentStatus).

2. Method Structure and Organization

    Functional Grouping: Group methods by functionality. For example, initialization methods (like LoadSeriesTable) should be grouped together and separated from processing methods (like UpdateSeriesDataTable).
    Entry Points: Use a dedicated entry point method (like Main or Start) to initialize and kick off the primary tasks.
    Task-Specific Methods: Encapsulate logic specific to tasks (e.g., StartDownloadsTask, CleanEncodedFilesTask) within dedicated methods or delegate expressions.

3. Encapsulation and Dependency Management

    Private Fields: Use private fields to encapsulate all internal state and helper objects (e.g., downloader, SeriesTable). This protects state and reduces dependency exposure.
    Minimize Global Dependencies: If a global or static dependency is needed, encapsulate access through methods rather than directly embedding logic that relies on the global instance (e.g., Global.TaskAdmin.Logger.Log).
    Constructor Initialization: Initialize all required dependencies within the constructor. Avoid hardcoding initialization within methods if a dependency could be reused elsewhere or may need to be configured differently.

4. Asynchronous Programming

    Use async/await for asynchronous tasks wherever possible to avoid blocking the main thread (e.g., await GetWebDataFromUrl).
    Return Task types: Methods performing asynchronous operations should return Task or Task<T> to allow calling code to manage and await them if needed.
    Rate Limiting: Use rate-limiting logic to prevent overloading remote services (e.g., throttling requests in GetWebDataFromUrl).

5. Error Handling

    Try-Catch Blocks: Use try-catch around code prone to exceptions, such as network calls or file operations (e.g., GetWebDataFromUrl, LoadSeriesTable).
    Logging Errors: Log exceptions with sufficient context (method name and error message) using Global.TaskAdmin.Logger.EX_Log.
    Fail Gracefully: Ensure the program continues operation where possible, even after an exception. Where not possible, notify the user and terminate gracefully.

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

8. Console and Feedback Management

    Real-Time Console Updates: Use PrintUpdateTable to update the console in a single, coordinated output for clear, real-time feedback on program state.
    Formatting: Align strings using helper methods (e.g., MatchStringLenghtWithSpaces) to ensure consistent formatting across console updates.
    Limit Output: Minimize extraneous output in loops or asynchronous methods to prevent console clutter.

9. Regex and Parsing

    Use of Regular Expressions: When using regular expressions (e.g., for episode number extraction), encapsulate regex patterns in constants or provide descriptive comments to clarify their purpose.
    Parsing and Validation: Validate parsed values, such as ProbableEpNumber, and handle cases where parsing fails gracefully.

10. Logging and Queueing Operations

    Operations Queue: Use Global.currentOpsQueue to manage queued status messages for real-time feedback.
    Priority Logging: Use logging selectively, ensuring high-priority actions like task start, task end, and errors are logged.

11. Code Readability

    Use Comments for Complex Logic: Add comments to explain any complex or non-obvious logic, especially in areas involving regular expressions or file operations.
    Consistent Spacing: Use consistent line spacing between methods and within methods to enhance readability.
    Limit Nesting: Aim to minimize deeply nested logic by splitting complex operations into smaller methods or using guard clauses.

By following this style guide, contributions to AniDownloader will be consistent, modular, and maintainable. The focus on asynchronous design, modular task handling, and clear logging will keep the program efficient and manageable for continuous execution in a terminal or daemon context.
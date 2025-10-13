# Code Style and Conventions

## General Conventions

### Indentation and Formatting
- **Indentation**: 4 spaces for C# files
- **Tab Width**: 4
- **Max Line Length**: 120 characters
- **Line Endings**: CRLF (Windows standard)
- **Final Newline**: Always insert at end of file

### Braces and Line Breaks
- New line before open brace for all constructs (`csharp_new_line_before_open_brace = all`)
- New line before catch, else, finally blocks
- Preserve single-line blocks and statements

## Naming Conventions

### Types and Namespaces
- **Classes, Structs, Enums**: PascalCase
- **Interfaces**: IPascalCase (prefix with I)
- **Type Parameters**: TPascalCase (prefix with T)

### Members
- **Methods**: PascalCase
- **Properties**: PascalCase
- **Events**: PascalCase
- **Local Functions**: PascalCase

### Fields
- **Public Fields**: PascalCase
- **Private Fields**: _camelCase (prefix with underscore)
- **Private Static Fields**: _camelCase (prefix with underscore)
- **Constants**: PascalCase (public and private)
- **Static Readonly Fields**: PascalCase

### Variables and Parameters
- **Local Variables**: camelCase
- **Local Constants**: camelCase
- **Parameters**: camelCase

## C# Language Preferences

### Type Usage
- **var**: Do not use var; prefer explicit types
- **this**: Do not use `this.` qualifier unless necessary

### Expression Preferences
- Prefer expression-bodied members for accessors, indexers, properties
- Do not use expression-bodied members for constructors, methods, operators
- Prefer pattern matching over `as` with null checks
- Prefer pattern matching over `is` with cast checks
- Prefer switch expressions

### Null Handling
- Use conditional delegate calls (`?.Invoke`)
- Prefer null propagation (`?.`)
- Prefer `is null` checks over reference equality method

### Code Block Preferences
- Always use braces for control flow statements
- Prefer simple using statements
- Use `readonly` for fields that don't change (warning level)

### Modifiers
- Prefer static local functions (warning level)
- Order: public, private, protected, internal, static, extern, new, virtual, abstract, sealed, override, readonly, unsafe, volatile, async

## Project-Specific Conventions

### Namespace Organization
- Using directives should be outside namespace
- Separate import directive groups
- Sort System directives first

### Code Quality
- **Nullable Reference Types**: Enabled project-wide
- **Treat Warnings as Errors**: true (all warnings must be addressed)
- **Implicit Usings**: Enabled

### Disabled Analyzers
The following StyleCop/analyzer rules are disabled or downgraded:
- SA0001, SA1010, SA1600, CS1591: Documentation rules
- SA1101: Prefix local calls with this
- SA1309: Field names must not begin with underscore (custom rule used instead)
- SA1633: File must have header
- SA1649: File name must match type name

### ReSharper Conventions
- Space in single-line accessor holders
- Space between accessors in single-line properties
- Trailing commas in multiline lists
- Attributes on separate lines (not same line as type/member)

## Async/Await
- Use async/await consistently throughout the codebase
- Method names ending with "Async" for asynchronous operations
- Prefer `Task` and `Task<T>` return types

## MongoDB-Specific Patterns
- Repository pattern for data access
- Use of MongoDB aggregation pipeline builders
- Custom BSON serializers for OctoMesh types (CkId, ModelId, OctoObjectId, etc.)
- Session-based data access (OctoAdminSession, OctoUserSession)

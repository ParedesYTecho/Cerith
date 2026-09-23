# Dependency Policy

## Rule

Dependencies are liabilities as well as capabilities.

Every new runtime package must justify:

- capability provided;
- why .NET/Windows APIs or existing dependencies are insufficient;
- runtime footprint;
- maintenance health;
- security implications;
- license compatibility.

## Avoid

- duplicate libraries solving the same problem;
- packages for trivial helpers;
- abandoned packages;
- transitive dependency explosions;
- frameworks added for one component;
- web runtime dependencies in always-on surfaces.

## Record

Significant dependency decisions should be recorded in `.agent/DECISIONS.md` or an ADR.

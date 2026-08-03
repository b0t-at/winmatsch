# JSON Schema Test Suite — Draft-07 subset

These test-only fixtures are copied from the
[JSON Schema Test Suite](https://github.com/json-schema-org/JSON-Schema-Test-Suite)
at commit `fb7372e8763a1417bddc65fa4c911b3e79b57b65`.
Line endings and trailing whitespace are normalized to repository conventions;
the JSON values are unchanged.

Included files exercise the keywords implemented by WinMatsch's intentionally
limited Draft-07 validator: `const`, `default`, `definitions`, `enum`, `format`,
`items`, `maxItems`, `maxLength`, `maximum`, `minItems`, `minLength`, `minimum`,
`not`, `oneOf`, `pattern`, `properties`, `$ref`, `required`, `type`, and
`uniqueItems`.

The runner rejects and counts groups that use valid Draft-07 features outside
WinMatsch's supported subset, such as tuple-form `items`, `additionalProperties`,
remote references, or recursive references. Every group accepted by the
load-time keyword gate must pass all of its test cases.

The upstream suite is MIT licensed. Its license is preserved in `LICENSE.txt`.
These fixtures are copied only to the test output and are not included in
WinMatsch release artifacts.

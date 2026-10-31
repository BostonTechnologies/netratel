from pathlib import Path

p = Path('src/NetRatel/NetRatel.Tests/API/McpOperatorPolicyMutationEndpointTests.cs')
s = p.read_text()
old = 'string environment = Environments.Development)'
assert s.count(old) == 1, 'Expected exactly one environment default'
p.write_text(s.replace(old, 'string environment = "Development")'))

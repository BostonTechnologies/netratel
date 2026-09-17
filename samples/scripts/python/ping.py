#| NetRatel-MANIFEST
# yaml
name: Ping
version: 1
params:
  - { name: Host, type: string, required: true }
  - { name: Count, type: int, required: false, default: "4" }
#| END

print("pinging", Host, "x", Count)

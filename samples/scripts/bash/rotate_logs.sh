#| NetRatel-MANIFEST
{
  "name": "Rotate-Logs",
  "version": 1,
  "params": [
    { "name": "Dir", "type": "string", "required": true },
    { "name": "Days", "type": "int", "required": false, "default": "7" }
  ]
}
#| END

echo "Rotating $Dir older than $Days days"

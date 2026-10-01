#!/usr/bin/env bash

oidc_smoke_claims_for_subject() {
  local login_page="$1" username="$2"
  node -e '
    const fs = require("node:fs");
    const html = fs.readFileSync(process.argv[1], "utf8");
    const profileBlock = html.match(/<script id="oidc-claim-profiles" type="application\/json">([\s\S]*?)<\/script>/);
    if (!profileBlock) process.exit(2);
    const profiles = JSON.parse(profileBlock[1]);
    const claims = profiles[process.argv[2]];
    if (!claims || typeof claims.sub !== "string" || !claims.sub ||
        typeof claims.preferred_username !== "string" || !claims.preferred_username ||
        !Array.isArray(claims.aud) || claims.aud.length === 0 ||
        (claims.roles !== undefined && !Array.isArray(claims.roles))) process.exit(3);
    process.stdout.write(JSON.stringify(claims));
  ' "$login_page" "$username"
}

verify_oidc_smoke_token_claims() {
  local token="$1" expected_claims="$2" username="$3" token_kind="$4" expected_issuer="$5"
  node -e '
    const [token, expectedJson, username, tokenKind, expectedIssuer] = process.argv.slice(1);
    const fail = () => {
      process.stderr.write(`The disposable OIDC provider returned invalid ${tokenKind} claims for ${username}.\n`);
      process.exit(1);
    };
    try {
      const segments = token.split(".");
      if (segments.length !== 3 || !expectedIssuer) fail();
      const claims = JSON.parse(Buffer.from(segments[1], "base64url").toString("utf8"));
      const expected = JSON.parse(expectedJson);
      const values = value => value == null ? [] : Array.isArray(value) ? value : [value];
      const sameValues = (actual, wanted) => {
        const normalizedActual = [...values(actual)].sort();
        const normalizedWanted = [...values(wanted)].sort();
        return JSON.stringify(normalizedActual) === JSON.stringify(normalizedWanted);
      };
      if (typeof expected.sub !== "string" || !expected.sub ||
          typeof expected.preferred_username !== "string" || !expected.preferred_username ||
          !Array.isArray(expected.aud) || expected.aud.length === 0 ||
          (expected.roles !== undefined && !Array.isArray(expected.roles)) ||
          claims.iss !== expectedIssuer || claims.sub !== expected.sub ||
          claims.preferred_username !== expected.preferred_username ||
          !sameValues(claims.aud, expected.aud) || !sameValues(claims.roles, expected.roles) ||
          !Number.isInteger(claims.exp) || claims.exp <= Math.floor(Date.now() / 1000)) fail();
    } catch {
      fail();
    }
  ' "$token" "$expected_claims" "$username" "$token_kind" "$expected_issuer"
}

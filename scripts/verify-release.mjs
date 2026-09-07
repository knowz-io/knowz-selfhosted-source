#!/usr/bin/env node
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

/** Anonymous metadata checks only. Does not download image layers or authenticate a customer. */
export async function verifySelfhostedRelease(
  version,
  { fetch: request = fetch } = {},
) {
  if (!/^\d+\.\d+\.\d+(?:-[\w.-]+)?$/.test(version))
    throw new Error("Expected an immutable selfhosted semver");
  const digests = {};
  const platforms = {};
  for (const component of ["api", "web", "mcp"]) {
    const repository = `knowz-io/knowz-selfhosted-${component}`;
    const tokenResponse = await request(
      `https://ghcr.io/token?service=ghcr.io&scope=repository:${repository}:pull`,
      { signal: AbortSignal.timeout(15000) },
    );
    if (!tokenResponse.ok)
      throw new Error(
        `Anonymous ${component} access refused (${tokenResponse.status})`,
      );
    const { token } = await tokenResponse.json();
    if (!token)
      throw new Error(`Anonymous ${component} registry token unavailable`);
    const response = await request(
      `https://ghcr.io/v2/${repository}/manifests/${version}`,
      {
        signal: AbortSignal.timeout(15000),
        headers: {
          Authorization: `Bearer ${token}`,
          Accept:
            "application/vnd.oci.image.index.v1+json,application/vnd.docker.distribution.manifest.list.v2+json",
        },
      },
    );
    if (!response.ok)
      throw new Error(
        `Selfhosted ${component}:${version} unavailable (${response.status})`,
      );
    const digest = response.headers.get("docker-content-digest");
    if (!/^sha256:[a-f0-9]{64}$/.test(digest ?? ""))
      throw new Error(`Selfhosted ${component} immutable digest unavailable`);
    const index = await response.json();
    const available = (index.manifests ?? [])
      .map((m) => m.platform)
      .filter((p) => p?.os === "linux")
      .map((p) => p.architecture);
    for (const arch of ["amd64", "arm64"])
      if (!available.includes(arch))
        throw new Error(
          `Selfhosted ${component}:${version} is missing linux/${arch}`,
        );
    digests[component] = digest;
    platforms[component] = ["linux/amd64", "linux/arm64"];
  }
  return {
    version,
    status: "complete",
    flavors: ["selfhosted"],
    digests,
    platforms,
    runtime: { database: "postgres", composeAsset: "docker-compose.yml" },
  };
}
if (
  process.argv[1] &&
  resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
  try {
    const result = await verifySelfhostedRelease(process.argv[2]);
    const output = JSON.stringify(result, null, 2) + "\n";
    if (process.argv[3]) writeFileSync(process.argv[3], output);
    else process.stdout.write(output);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}

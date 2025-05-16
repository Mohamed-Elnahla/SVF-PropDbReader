# Instructions

## 1. Create a .NET 8 Library Project

- Use `dotnet new classlib -n SVF.PropDbReader -f net8.0` to create a new .NET 8 class library project.

## 2. Create the following classes

- `PropDbReader`
- `DbDownloader`

## 3. Implementation Details

- The logic should be based on the provided Node.js application, specifically:
  - Downloading the property database using Autodesk Model Derivative API (use `Autodesk.ModelDerivative` NuGet).
  - Use `Autodesk.Authentication` for token management.
  - Use `APSToolkit` and any additional NuGets as needed.
- The `DbDownloader` class should encapsulate the logic for:
  - Authenticating with Autodesk.
  - Fetching the manifest.
  - Locating the property database derivative.
  - Downloading the property database file using signed cookies (update logic to use Autodesk .NET SDKs).
  - Caching the downloaded file and validating its integrity.
- The `PropDbReader` class should encapsulate:
  - Opening the SQLite property database.
  - Querying properties by dbId.
  - Merging parent properties recursively.
  - Exposing methods to retrieve properties for a given dbId.

## 4. NuGet Packages

- Add the following NuGet packages to the project:
  - `Autodesk.ModelDerivative`
  - `Autodesk.Authentication`
  - `APSToolkit`
  - `Microsoft.Data.Sqlite` (for SQLite access)
  - Any other required packages

## 5. Exclude

- Do not implement fragment enumeration or SVF reading logic.
- Focus only on the property database download and property querying logic.

## 6. Output

- The classes should be implemented in C# and follow .NET conventions.
- Provide clear method signatures and XML documentation where appropriate.

## 7. Old Node.js Code

- The old Node.js code is provided for reference. It should not be directly translated but used as a guide for the logic and flow.

```Node.js
// extractor.js (ESM version)
import dotenv from "dotenv";
dotenv.config(); // Load environment variables

import { downloadDerivativeWithCookies } from "./DBdownloader.js";
import {
  SvfReader,
  BasicAuthenticationProvider,
  TwoLeggedAuthenticationProvider,
} from "svf-utils";
import fs from "fs";
import path from "path";
import os from "os";
import { promises as fsp } from "fs";
import {
  SdkManagerBuilder,
  AuthClientConfiguration,
} from "@aps_sdk/autodesk-sdkmanager";
import { AuthenticationClient, Scopes } from "@aps_sdk/authentication";
import {
  ModelDerivativeClient as ModelDerivative2,
  ManifestHelper,
} from "aps-sdk-node";
import { ModelDerivativeClient } from "@aps_sdk/model-derivative";
import { fileURLToPath } from "url";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

/**
 * Helper to sanitize URN strings to safe filenames.
 */
function sanitizeFilename(urn) {
  return urn.replace(/[^a-z0-9]/gi, "_").toLowerCase();
}

/**
 * Checks if the file at filePath exists and is larger than a minimal size.
 */
async function isFileValid(filePath) {
  try {
    const stats = await fsp.stat(filePath);
    // Assume a valid file is at least 100 bytes.
    return stats.size > 100;
  } catch (err) {
    return false;
  }
}

/**
 * Downloads the properties database file for the given URN.
 * Uses a cache folder (./dbcache) keyed by sanitized URN so that multiple requests for the same URN
 * do not overwrite each other. If a valid cache file exists, it is re‑used.
 *
 * A request‑specific temporary copy is then created from the cache file and its path is returned.
 *
 * In case the download is incomplete or corrupted, the temporary file is not created.
 */
async function downloadPropertiesDatabase(urn, clientId, clientSecret, region) {
  // Setup a cache directory for property databases.
  const cacheDir = path.join(__dirname, "dbcache");
  try {
    await fsp.mkdir(cacheDir, { recursive: true });
  } catch (err) {
    console.error("Error creating cache directory:", err);
  }

  const cacheFile = path.join(
    cacheDir,
    `${sanitizeFilename(urn)}_properties.sdb`
  );

  // Check if a valid cached file exists.
  if (await isFileValid(cacheFile)) {
    console.log("Using cached property database at", cacheFile);
  } else {
    console.log(
      "No valid cache found. Downloading new property database for URN:",
      urn
    );

    const authenticationClient = new AuthenticationClient(
      SdkManagerBuilder.create().build()
    );
    const credentials = await authenticationClient.getTwoLeggedToken(
      clientId,
      clientSecret,
      [Scopes.DataRead, Scopes.ViewablesRead]
    );
    const derivativeClient = new ModelDerivative2(
      {
        client_id: clientId,
        client_secret: clientSecret,
      },
      "https://developer.api.autodesk.com",
      region
    );
    const manifest = await derivativeClient.getManifest(urn);
    const manifestHelper = new ManifestHelper(manifest);
    const pdbDerivatives = manifestHelper.search({
      type: "resource",
      role: "Autodesk.CloudPlatform.PropertyDatabase",
    });
    if (pdbDerivatives.length === 0) {
      console.log("No property database found in the manifest.");
      return null;
    }
    console.log("Found property database in the manifest.");
    console.log("Using property database GUID:", pdbDerivatives[0].guid);
    const pdbAsset = pdbDerivatives[0];
    console.log("Trying to download asset URN:", pdbAsset.urn);

    // Download to a temporary file first to ensure the download completes fully.
    const tempDownloadPath = path.join(
      cacheDir,
      `${sanitizeFilename(urn)}_temp.sdb`
    );
    await downloadDerivativeWithCookies(
      urn,
      pdbAsset.urn,
      credentials.access_token,
      cacheDir,
      path.basename(tempDownloadPath)
    );

    if (await isFileValid(tempDownloadPath)) {
      // Rename the temp file to the cache file.
      await fsp.rename(tempDownloadPath, cacheFile);
      console.log("Downloaded property database saved to cache at", cacheFile);
    } else {
      console.error(
        "Downloaded property database appears to be corrupted:",
        tempDownloadPath
      );
      return null;
    }
  }

  // Create a request‑specific temporary copy so that concurrent requests do not interfere.
  const tempFile = path.join(
    os.tmpdir(),
    `${sanitizeFilename(urn)}_${Date.now()}_${Math.random()
      .toString()
      .slice(2)}_properties.sdb`
  );
  await fsp.copyFile(cacheFile, tempFile);
  console.log("Using temporary property database copy at", tempFile);
  return tempFile;
}

/**
 * Opens the SQLite database and prepares a statement for querying properties by dbId.
 * Returns an object containing the database connection and the prepared statement.
 */
async function openPropertiesDatabase(dbPath) {
  const sqlite3 = (await import("sqlite3")).default.verbose();
  const { open } = await import("sqlite");
  const db = await open({
    filename: dbPath,
    driver: sqlite3.Database,
  });

  // Prepare a statement to fetch properties for a specific dbId.
  const stmt = await db.prepare(`
    SELECT _objects_attr.category AS catDisplayName,
           _objects_attr.display_name AS attrDisplayName,
           _objects_val.value AS propValue
    FROM _objects_eav
      INNER JOIN _objects_id ON _objects_eav.entity_id = _objects_id.id
      INNER JOIN _objects_attr ON _objects_eav.attribute_id = _objects_attr.id
      INNER JOIN _objects_val ON _objects_eav.value_id = _objects_val.id
    WHERE _objects_id.id = ?
  `);

  return { db, stmt };
}

/**
 * Given a prepared statement and a dbId, returns a mapping of "Category_Property" to value.
 */
async function getPropertiesForDbId(stmt, dbId) {
  const rows = await stmt.all(dbId);
  const props = {};
  for (const row of rows) {
    const key = `${row.catDisplayName}_${row.attrDisplayName}`;
    props[key] = row.propValue;
  }
  return props;
}

/**
 * Recursively merges properties from all ancestors.
 * If the current properties object contains a "__parent___null" key,
 * it retrieves the parent's properties and merges in any missing keys,
 * then continues up the chain until no parent is found.
 */
async function mergeParentProperties(childProps, stmt, propertiesCache) {
  if (childProps.hasOwnProperty("__parent___null")) {
    const parentDbId = childProps["__parent___null"];
    if (parentDbId && stmt) {
      let parentProps = {};
      if (propertiesCache.has(parentDbId)) {
        parentProps = propertiesCache.get(parentDbId);
      } else {
        parentProps = await getPropertiesForDbId(stmt, parentDbId);
        propertiesCache.set(parentDbId, parentProps);
      }
      // Recursively merge parent's properties.
      parentProps = await mergeParentProperties(
        parentProps,
        stmt,
        propertiesCache
      );
      // Merge any missing keys from the parent into the child.
      for (const [key, value] of Object.entries(parentProps)) {
        if (!(key in childProps)) {
          childProps[key] = value;
        }
      }
    }
  }
  return childProps;
}

/**
 * Retrieves SVF derivatives from the manifest.
 */
async function getSvfDerivatives(urn, clientId, clientSecret, region) {
  const authenticationClient = new AuthenticationClient(
    SdkManagerBuilder.create().build()
  );
  const modelDerivativeClient = new ModelDerivativeClient();
  const credentials = await authenticationClient.getTwoLeggedToken(
    clientId,
    clientSecret,
    [Scopes.ViewablesRead]
  );
  const manifest = await modelDerivativeClient.getManifest(urn, {
    accessToken: credentials.access_token,
    region,
  });
  const derivatives = [];
  function traverse(derivative) {
    if (
      derivative.type === "resource" &&
      derivative.role === "graphics" &&
      derivative.mime === "application/autodesk-svf"
    ) {
      derivatives.push(derivative);
    }
    if (derivative.children) {
      for (const child of derivative.children) {
        traverse(child);
      }
    }
  }
  for (const derivative of manifest.derivatives) {
    if (derivative.children) {
      for (const child of derivative.children) {
        traverse(child);
      }
    }
  }
  return derivatives;
}

/**
 * This function streams the extracted data as JSON chunks via the provided HTTP response.
 * It uses a request‑specific temporary copy of the properties database.
 *
 * After a successful response the temporary copy is deleted.
 * In case of an error, the temporary file is left intact.
 */
async function streamExtractedData(res, urn) {
  const APS_CLIENT_ID = process.env.APS_CLIENT_ID;
  const APS_CLIENT_SECRET = process.env.APS_CLIENT_SECRET;
  const APS_REGION = process.env.APS_REGION || "US";

  const authProvider = new TwoLeggedAuthenticationProvider(
    APS_CLIENT_ID,
    APS_CLIENT_SECRET
  );

  const derivatives = await getSvfDerivatives(
    urn,
    APS_CLIENT_ID,
    APS_CLIENT_SECRET,
    APS_REGION
  );
  if (!derivatives.length)
    throw new Error("No derivatives found for the given URN.");
  const guid = derivatives[0].guid;
  console.log("Using derivative GUID:", guid);

  // Download (or retrieve) the properties database and get a request‑specific temporary copy.
  let tempDbPath = await downloadPropertiesDatabase(
    urn,
    APS_CLIENT_ID,
    APS_CLIENT_SECRET,
    APS_REGION
  );
  if (!tempDbPath) {
    throw new Error("Unable to obtain a valid properties database.");
  }

  let db = null;
  let stmt = null;
  try {
    ({ db, stmt } = await openPropertiesDatabase(tempDbPath));
  } catch (error) {
    console.error("Error opening properties database:", error);
    // Do not remove the temporary file on error.
    throw error;
  }

  const reader = await SvfReader.FromDerivativeService(urn, guid, authProvider);
  const propertiesCache = new Map();
  let successful = false; // flag to mark if streaming completed successfully

  try {
    for await (const fragment of reader.enumerateFragments()) {
      const transform = fragment.transform;
      if (!transform || !transform.t) continue;
      const CenterPoint = transform.t;
      const bbox = fragment.bbox;
      const Location = {
        x: CenterPoint.x,
        y: CenterPoint.y,
        z: CenterPoint.z,
        minX: bbox[0],
        minY: bbox[1],
        minZ: bbox[2],
        maxX: bbox[3],
        maxY: bbox[4],
        maxZ: bbox[5],
      };
      const dbId = fragment.dbID ? fragment.dbID.toString() : null;
      let props = {};
      if (dbId && stmt) {
        if (propertiesCache.has(dbId)) {
          props = propertiesCache.get(dbId);
        } else {
          props = await getPropertiesForDbId(stmt, dbId);
          propertiesCache.set(dbId, props);
        }
        // Recursively merge all parent properties into the child.
        props = await mergeParentProperties(props, stmt, propertiesCache);
      }
      const dbIdInt = parseInt(dbId, 10);
      // Include the urn field in each JSON object.
      const elementData = {
        urn,
        Identity_dbId: dbIdInt,
        Location,
        ...props,
      };
      // Write out each JSON object followed by a newline.
      res.write(JSON.stringify(elementData) + "\n");

      // If a flush function is available, call it to send the chunk immediately.
      if (typeof res.flush === "function") {
        res.flush();
      }
    }
    res.end();
    successful = true; // mark successful completion
  } catch (error) {
    console.error("Error during streaming:", error);
    throw error;
  } finally {
    if (stmt) await stmt.finalize();
    if (db) await db.close();
    if (successful) {
      try {
        await fsp.unlink(tempDbPath);
        const cacheDir = path.join(__dirname, "dbcache");
        const cacheFile = path.join(
          cacheDir,
          `${sanitizeFilename(urn)}_properties.sdb`
        );
        await fsp.unlink(cacheFile);
        console.log("Cache file removed:", cacheFile);
        console.log("Temporary properties database removed:", tempDbPath);
      } catch (err) {
        console.error("Error removing temporary properties database:", err);
      }
    }
  }
}

export { streamExtractedData };
```

```Node.js
// DBdownloader.js
import fetch from "node-fetch";
import fs from "fs";
import { promisify } from "util";
import { pipeline } from "stream";
import path from "path";

const streamPipeline = promisify(pipeline);

export async function downloadDerivativeWithCookies(
  urn,
  derivativeUrn,
  token,
  savePath,
  filename
) {
  const signedCookiesUrl = `https://developer.api.autodesk.com/modelderivative/v2/designdata/${encodeURIComponent(
    urn
  )}/manifest/${encodeURIComponent(derivativeUrn)}/signedcookies`;

  const cookieResponse = await fetch(signedCookiesUrl, {
    method: "GET",
    headers: {
      Authorization: `Bearer ${token}`,
    },
  });

  if (!cookieResponse.ok) {
    throw new Error(
      `Failed to get signed cookies: ${cookieResponse.statusText}`
    );
  }

  const json = await cookieResponse.json();
  const fileUrl = json.url;

  const cookies = cookieResponse.headers
    .raw()
    ["set-cookie"].map((c) => c.split(";")[0])
    .join("; ");

  const filePath = path.join(savePath, filename);

  const downloadResponse = await fetch(fileUrl, {
    headers: {
      Cookie: cookies,
    },
  });

  if (!downloadResponse.ok) {
    throw new Error(`File download failed: ${downloadResponse.statusText}`);
  }

  await streamPipeline(downloadResponse.body, fs.createWriteStream(filePath));
  console.log(`File saved to ${filePath}`);
}
```

# CraneCAN Project Package — internal SHA-256 manifest

CraneCAN project ZIP packages now contain a required root file:

`CraneCAN.package-manifest.json`

It is generated during **Export ZIP** after Project Integrity and staged-copy checks pass.

## Purpose

The internal manifest records, for every package payload file:

- normalized package path;
- exact byte length;
- SHA-256 digest.

The payload set includes the root `*.canproject` plus every registered project resource (`*.trc`, `*.canincident`, `*.craneprofile`, reports, documents, snapshots, experiments, etc.). The SHA-256 manifest intentionally does **not** contain a hash of itself, avoiding recursive self-hashing.

The manifest also records:

- schema version;
- package format identifier;
- root `*.canproject` filename;
- CraneCAN `projectId`.

## Export verification

Before the ZIP is published CraneCAN:

1. checks source Project Integrity;
2. copies only registered resources to a private staging directory;
3. saves the staged `*.canproject`;
4. checks staged Project Integrity;
5. hashes every payload file;
6. writes `CraneCAN.package-manifest.json`;
7. creates the ZIP;
8. reopens the ZIP and verifies every entry against the staged bytes.

The source project and source resources remain unchanged.

## Import verification

**Import ZIP** requires the internal SHA-256 manifest. A legacy package without it is rejected with an instruction to re-export it using the current CraneCAN version.

Before a destination project is published CraneCAN:

1. validates ZIP paths and rejects traversal, absolute paths, ADS-style names, Windows reserved names, symbolic links, duplicates and file/directory collisions;
2. requires exactly one root `*.canproject` and exactly one root `CraneCAN.package-manifest.json`;
3. reads and validates the internal manifest;
4. extracts and hashes the root `*.canproject` and compares it with the stored SHA-256/length **before loading it**;
5. verifies that the hash-manifest file list exactly matches the `*.canproject` resources;
6. extracts every resource and compares its actual byte length and SHA-256 with the values recorded at export time;
7. runs Project Integrity on the staged project;
8. only then atomically moves the staged directory to the requested destination.

Any mismatch prevents publication of the imported project.

## Security boundary

SHA-256 provides strong integrity detection for accidental changes and for payload modification when the recorded manifest is not also replaced. It is **not a digital signature** and does not prove who created the package. A party able to modify both payload files and the internal manifest can create a different internally consistent package.

Authenticity/signature support, if required later, must be a separate feature using a trusted signing key and signature verification.

## Safety

This feature is file-only and offline. It adds no CAN transmit path and does not change the receive-only/listen-only safety model of CraneCAN.

# wingood

wingood helps a person update installed Windows apps with less time spent waiting for downloads.

## Language

**App**:
An installed program the user may want to update. An app can have more than one installed copy, such as separate copies for one user and for the whole computer.
_Avoid_: Product, workload

**Update**:
Moving an installed app to a newer available version.
_Avoid_: Install, when referring to the whole update

**Batch**:
The collection of app updates selected for one invocation of wingood.
_Avoid_: Job, campaign

**Download**:
Retrieving the files needed to update an app.

**Installation**:
Applying an app update on the computer, including the preparation and finishing work that Windows reports with it.

**Waiting to install**:
The time after an app's download has finished and before its installation begins.
_Avoid_: Downloading, for this waiting period

**Pin**:
A user-defined rule that blocks an app update or limits which versions are allowed.

**Update requiring explicit selection**:
An available app update that is excluded from a bulk update unless the user names the app specifically.
_Avoid_: User pin, because this exclusion is a separate rule

**Bulk update**:
A batch of all eligible app updates discovered for the selected sources and scope, subject to exclusions.
_Avoid_: Every installed app, since current apps and excluded updates are not part of this batch

**Retry**:
A new attempt at updates that failed in an earlier batch, after checking that an applicable update is still available.

**Hands-off update**:
An update the user has chosen to proceed with, after which wingood accepts required package and source agreements and avoids asking further questions. Windows administrator approval and prompts from an app's own installer may still require the user.

**Source**:
A catalog that provides app listings and update information, such as the WinGet community catalog or Microsoft Store.

**Package details**:
Information about an app's available release, including its description, publisher, license, links, tags, and installer information.

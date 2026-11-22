# Changelog

## 1.0.0 (2026-11-22)


### ⚠ BREAKING CHANGES

* "rules" is no longer read; config.json needs "include" and "exclude" lists instead.
* the dedupeThreshold setting is removed and ignored.
* databases created by earlier versions are not migrated. The app refuses to open them; delete capture.db (and its -wal/-shm files).
* **app:** read config.json from next to the executable
* **core:** remove the built-in default rules
* **app:** remove the --dump and --redaction-test modes

### Features

* add catch-all rule and never capture shell or own windows ([c5cdab1](https://github.com/sxvan/Worktrace/commit/c5cdab1f433d9dcd6db1955595933edd8d206793))
* add encryptImages setting and an Export day tray item ([9434340](https://github.com/sxvan/Worktrace/commit/94343407bb0a6eb6ad78c9bf12b3b267abd2e3ae))
* add log-only rule mode and rename non_work to other ([d0b8248](https://github.com/sxvan/Worktrace/commit/d0b82488938b206fcbd273bda25ec7858f97a263))
* add M1 monitoring with events table ([e7f7eeb](https://github.com/sxvan/Worktrace/commit/e7f7eeb2ccc8f1c91c0841b2af9a71f9f65135b6))
* add M3 allowlisted per-window capture and per-monitor compositing ([351de3e](https://github.com/sxvan/Worktrace/commit/351de3ea4e04d44dc5fd2474a64200ab78c167f5))
* add M4 per-window images, dedupe, captures tables and retention ([19a5555](https://github.com/sxvan/Worktrace/commit/19a55554b5b474c13eb5184df92a1e75f193ecbc))
* add M5 tray application, pause, autostart and single-file publish ([767ad55](https://github.com/sxvan/Worktrace/commit/767ad5584e04f30c50a89695180421dc2ea730ba))
* add OCR-based secret redaction with heuristic detector ([0a982b9](https://github.com/sxvan/Worktrace/commit/0a982b9ceb363a65b76c0453d5f65ad5cb4719a6))
* add timecapture-cli for daily export and reviewed screenshot sharing ([e987f9e](https://github.com/sxvan/Worktrace/commit/e987f9e380c57118e4994349b7d6df5b20eb5914))
* add timecapture-cli with daily export and reviewed screenshot sharing ([43845ac](https://github.com/sxvan/Worktrace/commit/43845ac5605b8fee0355f94e8a7c48179eb94e35))
* **app:** drive trigger engine from monitor signals and log capture decisions ([fc9c797](https://github.com/sxvan/Worktrace/commit/fc9c797f5045f435803112749cb81994c4979198))
* **app:** read config.json from next to the executable ([55a9c79](https://github.com/sxvan/Worktrace/commit/55a9c793e9b9e9d9c492790dc72496dc10514bda))
* compare captures with the last saved image at full resolution ([ea1e31e](https://github.com/sxvan/Worktrace/commit/ea1e31ee5a3ea11f020a545e102574f9455020e7))
* **core:** add capture trigger state machine with tests ([131651a](https://github.com/sxvan/Worktrace/commit/131651a4f4c3e5fd1af1fe4af36670781a918c9e))
* **core:** add region math, capture planner, compositor and redaction types ([7cd32cb](https://github.com/sxvan/Worktrace/commit/7cd32cb1a7c548c256089be5bd6707d5c434635e))
* **core:** detect more provider token formats ([71abff1](https://github.com/sxvan/Worktrace/commit/71abff1aca4fbe00bac0b5b36a096e55d7cbe319))
* **core:** find passwords from context and drop the Laya experiment ([1d96d0a](https://github.com/sxvan/Worktrace/commit/1d96d0a1df0a5337debb718bc0b7b1ad7e8bf522))
* encrypt stored images with per-day keys wrapped by DPAPI ([eb786c7](https://github.com/sxvan/Worktrace/commit/eb786c7dde5b9938d66c67587ceb6908afce5999))
* encrypt stored screenshots and lock down the storage folder ([7402a16](https://github.com/sxvan/Worktrace/commit/7402a16612dcd8fd576e05a9fee9fd9638dffc19))
* exclude windows from a rule by title, so websites can be left out ([4cb6d02](https://github.com/sxvan/Worktrace/commit/4cb6d023eae61c7609cd54fb277a457b7f4bbac5))
* keep scrubbed screen text as its own storage layer ([252e47b](https://github.com/sxvan/Worktrace/commit/252e47b54d9d3ccd36e911a2042060144470d911))
* make URL query stripping opt-in and document the layers ([5d85a8b](https://github.com/sxvan/Worktrace/commit/5d85a8b26d4d89867e1dcaaca35a6195775ad12d))
* **prototype:** add M0 per-window capture console app ([30da48b](https://github.com/sxvan/Worktrace/commit/30da48b97d51c341e0ac739628ea1a50573bb9db))
* read visits, screenshot ids and Claude Code sessions for a daily export ([e29dfbe](https://github.com/sxvan/Worktrace/commit/e29dfbee3d0393a0334a5ee2a95b73d7d034cc7a))
* redact text recognized on demand and find the image regions to black out ([c68d01e](https://github.com/sxvan/Worktrace/commit/c68d01ef7b464cc5de068967fe82f561091e31fb))
* restrict the storage root to the current user and warn about synced folders ([c2d3b76](https://github.com/sxvan/Worktrace/commit/c2d3b76086590cb7abc56800942dbc27bf72fab6))
* separate include and exclude rules ([148e168](https://github.com/sxvan/Worktrace/commit/148e1683a5e74da46f730b72038c19ab9a640a38))
* separate include and exclude rules ([820eb9c](https://github.com/sxvan/Worktrace/commit/820eb9c90b5e06ffaabf648024777d45a4466058))
* skip captures while a password field has focus ([6137c96](https://github.com/sxvan/Worktrace/commit/6137c96519aff32accb644f521afc975e03a5ce5))
* stop recognizing and redacting screen content at capture time ([618fc50](https://github.com/sxvan/Worktrace/commit/618fc50c9cf77558e84a9b61e5252ac61ca7b338))
* **storage:** scale saved images down to a configurable maximum size ([8c8b437](https://github.com/sxvan/Worktrace/commit/8c8b43703b2017982d91a0528d8e5aced437b914))
* store screenshots as lossless PNG at full size ([4c33e57](https://github.com/sxvan/Worktrace/commit/4c33e572e3847691bae9b9f4d19904ca722778fe))
* store screenshots as lossless PNG at full size ([5afbcad](https://github.com/sxvan/Worktrace/commit/5afbcad39421b88a280307076dc5d3bbed0b9b8a))
* store scrubbed OCR text and redact window titles ([ec00a46](https://github.com/sxvan/Worktrace/commit/ec00a4668aecc36a3851ccbcf5a24492d49c942a))


### Bug Fixes

* **core:** label coalesced captures as window_switch when one was involved ([c936ca4](https://github.com/sxvan/Worktrace/commit/c936ca46279db6da3c1552a9d1a82bfe34d8d86f))
* **core:** stop redacting ordinary prose that mentions passwords ([2f16b9a](https://github.com/sxvan/Worktrace/commit/2f16b9aad4d3489693ad6de04ce0091328cb8a28))
* never dedupe a window whose title changed, and say when a screenshot is reused ([514ad9e](https://github.com/sxvan/Worktrace/commit/514ad9ee2e0f733d5e7ba9816bc513368794e5f8))
* say why a visit is unrecorded, and report every activity with de-CH entry texts ([e2e4829](https://github.com/sxvan/Worktrace/commit/e2e4829ec42262f6da133fdb28f001267f7d2e95))
* silence Paddle logging and let a declined screenshot be followed by another ([6f772cf](https://github.com/sxvan/Worktrace/commit/6f772cfd545910ac3143116a3403135d76b124b7))
* split OCR rows at column gaps and fold short window switches into neighbours ([42aa388](https://github.com/sxvan/Worktrace/commit/42aa3886aeeab01f17e1f867a53fde6a75119179))
* stop invisible overlays from blacking out captured windows ([aa97057](https://github.com/sxvan/Worktrace/commit/aa9705702a91f73a783e1bbb5675e0ac90f3fd6d))
* stop invisible overlays from blacking out captured windows ([b9e9bdb](https://github.com/sxvan/Worktrace/commit/b9e9bdb547c1b3ec2ceb882b2c1651df9df0dc3d))
* **windows:** place redaction boxes exactly on the recognized words ([81de0ba](https://github.com/sxvan/Worktrace/commit/81de0baaf8fbf419643340f13d4806724685de89))


### Code Refactoring

* **app:** remove the --dump and --redaction-test modes ([dbb230c](https://github.com/sxvan/Worktrace/commit/dbb230c09e71b17aee0b76813c860ef607f13368))
* **core:** remove the built-in default rules ([c73c88e](https://github.com/sxvan/Worktrace/commit/c73c88e13f69cc77809485dea9ddaaf5f1ce2b47))

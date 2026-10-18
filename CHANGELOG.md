# Change Log

All notable changes to this project will be documented in this file. See [versionize](https://github.com/versionize/versionize) for commit guidelines.

<a name="5.3.0"></a>
## [5.3.0](https://www.github.com/EntglDb/BLite/releases/tag/v5.3.0) (2026-10-18)

### Features

* **index:** full-text search with BM25 ranking ([#70](https://www.github.com/EntglDb/BLite/issues/70)) ([#177](https://www.github.com/EntglDb/BLite/issues/177)) ([a562208](https://www.github.com/EntglDb/BLite/commit/a562208c43c1d3afb6fa1908b5ea75a8ac5a7155))
* **storage:** per-collection required features, refused by versions that lack them ([#187](https://www.github.com/EntglDb/BLite/issues/187)) ([#194](https://www.github.com/EntglDb/BLite/issues/194)) ([984b906](https://www.github.com/EntglDb/BLite/commit/984b9066eb48f53e12e1bda38c219bb4b7652b06))

### Bug Fixes

* **blql:** compare two integers exactly instead of through double ([#285](https://www.github.com/EntglDb/BLite/issues/285)) ([3af68f8](https://www.github.com/EntglDb/BLite/commit/3af68f81d272e1682f400849a0d53e6f0d9e8ad2))
* **blql:** First and FirstOrDefault honour Sort and Skip ([#284](https://www.github.com/EntglDb/BLite/issues/284)) ([c8c932d](https://www.github.com/EntglDb/BLite/commit/c8c932d6867446cc8750f1a6fe5f5c9ea5f818ff))
* **bson:** compare Int64 and date BsonValues by their stored bits ([#306](https://www.github.com/EntglDb/BLite/issues/306)) ([#315](https://www.github.com/EntglDb/BLite/issues/315)) ([e27f75f](https://www.github.com/EntglDb/BLite/commit/e27f75fc663895c695a17cc80bd574b5c9d74f6f))
* **collections:** don't roll back a caller-owned transaction on a failed write ([#185](https://www.github.com/EntglDb/BLite/issues/185)) ([#204](https://www.github.com/EntglDb/BLite/issues/204)) ([416fb5b](https://www.github.com/EntglDb/BLite/commit/416fb5b86247347c0211179396312aee4736997d))
* **generator:** discover a DocumentDbContext whatever its class name ([#300](https://www.github.com/EntglDb/BLite/issues/300)) ([#317](https://www.github.com/EntglDb/BLite/issues/317)) ([7184152](https://www.github.com/EntglDb/BLite/commit/718415278b4e7046d68c3dfa528f66e5303f8fec))
* **generator:** emit the mapper's BSON keys so typed writes work under Native AOT ([#278](https://www.github.com/EntglDb/BLite/issues/278)) ([202047d](https://www.github.com/EntglDb/BLite/commit/202047d10e099ad100f22c2596f6ea69d3173419))
* **generator:** support DbContext/entities declared in the global namespace ([#152](https://www.github.com/EntglDb/BLite/issues/152)) ([cb8a521](https://www.github.com/EntglDb/BLite/commit/cb8a521ffc6698848c717feed80504e1182d722b))
* **index:** -0.0 and +0.0 share one index key ([#249](https://www.github.com/EntglDb/BLite/issues/249)) ([#252](https://www.github.com/EntglDb/BLite/issues/252)) ([fba9330](https://www.github.com/EntglDb/BLite/commit/fba933012baf3bf814804d9da20ae28e0ccb77d7))
* **index:** a failed unique CreateIndexAsync on a DynamicCollection leaves no index behind ([#283](https://www.github.com/EntglDb/BLite/issues/283)) ([53f0380](https://www.github.com/EntglDb/BLite/commit/53f0380612d512e34f9fd89479f2119ed5d9c6b4))
* **index:** accept DateTime and DateTimeOffset bounds in QueryIndexAsync ([#286](https://www.github.com/EntglDb/BLite/issues/286)) ([07f4c5b](https://www.github.com/EntglDb/BLite/commit/07f4c5bc4febd512574a933e6c04f72a9cabd631))
* **index:** enforce unique secondary indexes on typed collections ([#176](https://www.github.com/EntglDb/BLite/issues/176)) ([#182](https://www.github.com/EntglDb/BLite/issues/182)) ([7dbe21d](https://www.github.com/EntglDb/BLite/commit/7dbe21de2aa71d452811c8cf7962a29f2e5e4fd7))
* **index:** express comparison bounds in the indexed property's type ([#222](https://www.github.com/EntglDb/BLite/issues/222)) ([#228](https://www.github.com/EntglDb/BLite/issues/228)) ([7e37f33](https://www.github.com/EntglDb/BLite/commit/7e37f33e3644b28b872e5c0bd3a61c3b2ceadefd))
* **index:** fill model-declared indexes from existing documents ([#171](https://www.github.com/EntglDb/BLite/issues/171)) ([#175](https://www.github.com/EntglDb/BLite/issues/175)) ([2d7b5cc](https://www.github.com/EntglDb/BLite/commit/2d7b5ccd55fee5b8e9a68b64c7fb2f88d4736c48))
* **index:** free B-Tree pages only when the transaction commits ([#233](https://www.github.com/EntglDb/BLite/issues/233)) ([#236](https://www.github.com/EntglDb/BLite/issues/236)) ([4d3e6b9](https://www.github.com/EntglDb/BLite/commit/4d3e6b9185e43fd587e6958e847aa0e019276ee5))
* **index:** keep equal B-tree keys in insertion order on a forward scan ([#281](https://www.github.com/EntglDb/BLite/issues/281)) ([bfb05bb](https://www.github.com/EntglDb/BLite/commit/bfb05bb831421397e705a49884fb60371576661e))
* **index:** keep every bound when an AND merges ranges on one index ([#223](https://www.github.com/EntglDb/BLite/issues/223)) ([#225](https://www.github.com/EntglDb/BLite/issues/225)) ([ffdaaba](https://www.github.com/EntglDb/BLite/commit/ffdaaba8abaf1800e46870bf7cf99183273f678d))
* **index:** match only the exact value in string index range bounds ([#206](https://www.github.com/EntglDb/BLite/issues/206)) ([#214](https://www.github.com/EntglDb/BLite/issues/214)) ([f8f437b](https://www.github.com/EntglDb/BLite/commit/f8f437b71e746ab46a71d30bb7f800b16ed1f003))
* **index:** rebuild model indexes whose definition changed, report undeclared ones, keep other processes' indexes on save ([#191](https://www.github.com/EntglDb/BLite/issues/191), [#192](https://www.github.com/EntglDb/BLite/issues/192)) ([#240](https://www.github.com/EntglDb/BLite/issues/240)) ([79b09e6](https://www.github.com/EntglDb/BLite/commit/79b09e6e7b2d9549c226b5b622d0183058ac2797))
* **index:** release index pages: idempotent FreePage, dynamic drop/rebuild frees, checkpoint on pending frees ([#238](https://www.github.com/EntglDb/BLite/issues/238), [#211](https://www.github.com/EntglDb/BLite/issues/211), [#209](https://www.github.com/EntglDb/BLite/issues/209)) ([#239](https://www.github.com/EntglDb/BLite/issues/239)) ([156ba68](https://www.github.com/EntglDb/BLite/commit/156ba689c80b91c5dd1d2bf8ed09c072aec58428))
* **index:** split B-Tree nodes by size and find duplicates across splits ([#217](https://www.github.com/EntglDb/BLite/issues/217), [#213](https://www.github.com/EntglDb/BLite/issues/213)) ([#232](https://www.github.com/EntglDb/BLite/issues/232)) ([70eb3c3](https://www.github.com/EntglDb/BLite/commit/70eb3c374087e7adb0d82dbf03d750aef6c543ed))
* **index:** update and delete spatial and vector index entries ([#164](https://www.github.com/EntglDb/BLite/issues/164)) ([#172](https://www.github.com/EntglDb/BLite/issues/172)) ([3984144](https://www.github.com/EntglDb/BLite/commit/398414408d667f010a8a5f82ba51550e049caa34))
* **linq:** apply Take, Skip and OrderBy in the order they are written ([#230](https://www.github.com/EntglDb/BLite/issues/230)) ([#235](https://www.github.com/EntglDb/BLite/issues/235)) ([33c6bbf](https://www.github.com/EntglDb/BLite/commit/33c6bbfc784e2787d9aac4661c4a1f790f73f964))
* **linq:** fold only root-chain Where calls into the fetch predicate ([#186](https://www.github.com/EntglDb/BLite/issues/186)) ([#229](https://www.github.com/EntglDb/BLite/issues/229)) ([6d6f38c](https://www.github.com/EntglDb/BLite/commit/6d6f38c53698c7a35b313589b124e7e51e190b16))
* **linq:** pass constants to cached rewriter delegates on every call ([#207](https://www.github.com/EntglDb/BLite/issues/207)) ([#224](https://www.github.com/EntglDb/BLite/issues/224)) ([708e820](https://www.github.com/EntglDb/BLite/commit/708e820c528af9015f5c50f36ee3a1f2b86ed719))
* **linq:** x.Prop != constant matches documents whose property is null or absent ([#248](https://www.github.com/EntglDb/BLite/issues/248)) ([#251](https://www.github.com/EntglDb/BLite/issues/251)) ([7b2a721](https://www.github.com/EntglDb/BLite/commit/7b2a721b52b22755780f30505f9a1fc9ce59ef0e))
* **mapper:** register, query and project renamed properties under their stored name ([#184](https://www.github.com/EntglDb/BLite/issues/184)) ([#231](https://www.github.com/EntglDb/BLite/issues/231)) ([87eddc4](https://www.github.com/EntglDb/BLite/commit/87eddc4377c2af77ae9bbe1e651e5e4358b3bc52))
* **query:** evaluate Near/Within for real and refine indexed Near to the circle ([#165](https://www.github.com/EntglDb/BLite/issues/165)) ([#173](https://www.github.com/EntglDb/BLite/issues/173)) ([efacaf4](https://www.github.com/EntglDb/BLite/commit/efacaf40734a53db3734dacc69983f9194487395))
* **query:** return the real result of terminals after Select and after an indexed top-N window ([#181](https://www.github.com/EntglDb/BLite/issues/181)) ([#193](https://www.github.com/EntglDb/BLite/issues/193)) ([519b20f](https://www.github.com/EntglDb/BLite/commit/519b20f3a5a70dd337032a46125e035b242a733c))
* **spatial:** cover the antimeridian and the poles in Near queries ([#166](https://www.github.com/EntglDb/BLite/issues/166)) ([#174](https://www.github.com/EntglDb/BLite/issues/174)) ([a61880e](https://www.github.com/EntglDb/BLite/commit/a61880ed441a095376fe37f527c6eddc4d21df4a))
* **spatial:** enumerate R-tree pages so spatial indexes are freed and rebuilt ([#189](https://www.github.com/EntglDb/BLite/issues/189)) ([#205](https://www.github.com/EntglDb/BLite/issues/205)) ([82deef3](https://www.github.com/EntglDb/BLite/commit/82deef3d62e274cfdedaf569f99d9ba6786d68cb))
* **storage:** discard pages from a WAL another process has truncated ([#197](https://www.github.com/EntglDb/BLite/issues/197)) ([#201](https://www.github.com/EntglDb/BLite/issues/201)) ([8db6a14](https://www.github.com/EntglDb/BLite/commit/8db6a14d42a12945d7b0be488ae5a62b3330abb4))
* **storage:** don't let lock-free metadata readers miss records a compaction moves (follow-up to [#196](https://www.github.com/EntglDb/BLite/issues/196)) ([#215](https://www.github.com/EntglDb/BLite/issues/215)) ([cf1dc57](https://www.github.com/EntglDb/BLite/commit/cf1dc574aa9df423baccc58a038296d676b1b9b8))
* **storage:** don't reuse freed pages that still have a WAL image ([#168](https://www.github.com/EntglDb/BLite/issues/168)) ([#169](https://www.github.com/EntglDb/BLite/issues/169)) ([e19035c](https://www.github.com/EntglDb/BLite/commit/e19035c2cd45cfaa8463f8a6befec315515fc0e9))
* **storage:** don't skip WAL records past a torn one in multi-process replay ([#198](https://www.github.com/EntglDb/BLite/issues/198)) ([#202](https://www.github.com/EntglDb/BLite/issues/202)) ([eac759f](https://www.github.com/EntglDb/BLite/commit/eac759fcb215cab3731c261ece2eebdada396f49))
* **storage:** drain only the flushed WAL image, atomically, after a checkpoint ([#311](https://www.github.com/EntglDb/BLite/issues/311)) ([#313](https://www.github.com/EntglDb/BLite/issues/313)) ([628057e](https://www.github.com/EntglDb/BLite/commit/628057ec016317b156cfd5bbf49c77e7e71a3659))
* **storage:** make the collection slot map safe for lock-free readers ([#312](https://www.github.com/EntglDb/BLite/issues/312)) ([#314](https://www.github.com/EntglDb/BLite/issues/314)) ([5ef9acc](https://www.github.com/EntglDb/BLite/commit/5ef9acc9d1b7f730037cd88ef530935be19e0bd8))
* **storage:** one WAL header and one truncation protocol for the shared WAL ([#203](https://www.github.com/EntglDb/BLite/issues/203), [#200](https://www.github.com/EntglDb/BLite/issues/200), [#199](https://www.github.com/EntglDb/BLite/issues/199)) ([#227](https://www.github.com/EntglDb/BLite/issues/227)) ([056909d](https://www.github.com/EntglDb/BLite/commit/056909df4b13cc26e38e1f0286a738cb3e0400cd))
* **storage:** reads without a transaction catch up with the shared WAL ([#241](https://www.github.com/EntglDb/BLite/issues/241)) ([#243](https://www.github.com/EntglDb/BLite/issues/243)) ([0450b97](https://www.github.com/EntglDb/BLite/commit/0450b9760d557942fd2b4f8e18320321f810a58c))
* **storage:** refuse to overwrite a collection record this version cannot parse ([#187](https://www.github.com/EntglDb/BLite/issues/187)) ([#219](https://www.github.com/EntglDb/BLite/issues/219)) ([d7f0dd7](https://www.github.com/EntglDb/BLite/commit/d7f0dd7ebb6fd7133fac0d61db21b0bb7f6012c7))
* **storage:** serialise multi-process create/open and remap grown files ([#216](https://www.github.com/EntglDb/BLite/issues/216)) ([#221](https://www.github.com/EntglDb/BLite/issues/221)) ([1de6526](https://www.github.com/EntglDb/BLite/commit/1de65264b461d4d1e78226cf42d8c3a5160e0895))
* **storage:** stop the collection metadata chain from growing with every save ([#195](https://www.github.com/EntglDb/BLite/issues/195)) ([#196](https://www.github.com/EntglDb/BLite/issues/196)) ([4250e29](https://www.github.com/EntglDb/BLite/commit/4250e29fd20fe7f686262ff5837cc50d4749a887))
* **storage:** wait for the pending-free checkpoint to finish before disposing the engine ([#209](https://www.github.com/EntglDb/BLite/issues/209)) ([#242](https://www.github.com/EntglDb/BLite/issues/242)) ([9b9c655](https://www.github.com/EntglDb/BLite/commit/9b9c6552e8354b7226913ee717139bb828c9cac1))
* **storage:** write a new page of an encrypted file at allocation so an uncommitted page decrypts ([#279](https://www.github.com/EntglDb/BLite/issues/279)) ([34849bc](https://www.github.com/EntglDb/BLite/commit/34849bcc710ebc235fb250fa61dabbec490a3e48))
* **vacuum:** rebuild typed-collection indexes into fresh trees ([#167](https://www.github.com/EntglDb/BLite/issues/167)) ([#170](https://www.github.com/EntglDb/BLite/issues/170)) ([870c729](https://www.github.com/EntglDb/BLite/commit/870c729a7ad0da5774ea229a263375822e014879))

### Breaking Changes

* **query:** evaluate Near/Within for real and refine indexed Near to the circle ([#165](https://www.github.com/EntglDb/BLite/issues/165)) ([#173](https://www.github.com/EntglDb/BLite/issues/173)) ([efacaf4](https://www.github.com/EntglDb/BLite/commit/efacaf40734a53db3734dacc69983f9194487395))

<a name="5.2.3"></a>
## [5.2.3](https://www.github.com/EntglDb/BLite/releases/tag/v5.2.3) (2026-10-12)

<a name="5.2.2"></a>
## [5.2.2](https://www.github.com/EntglDb/BLite/releases/tag/v5.2.2) (2026-10-12)

### Bug Fixes

* **storage:** survive a corrupt free-list head instead of faulting the process ([#178](https://www.github.com/EntglDb/BLite/issues/178)) ([31cacf3](https://www.github.com/EntglDb/BLite/commit/31cacf33faa86c7acb696072beb7db34ade41b59))

<a name="5.2.1"></a>
## [5.2.1](https://www.github.com/EntglDb/BLite/releases/tag/v5.2.1) (2026-10-04)

### Bug Fixes

* **storage:** prevent reader starvation under back-to-back WritePage calls ([#159](https://www.github.com/EntglDb/BLite/issues/159)) ([6b93354](https://www.github.com/EntglDb/BLite/commit/6b9335471f7f594f48bfe33ed4c21ea8a305dbbe))

<a name="5.2.0"></a>
## [5.2.0](https://www.github.com/EntglDb/BLite/releases/tag/v5.2.0) (2026-10-04)

### Bug Fixes

* **bson:** grow BsonDocumentBuilder buffer for nested values > 1 KB ([#154](https://www.github.com/EntglDb/BLite/issues/154)) ([7b00930](https://www.github.com/EntglDb/BLite/commit/7b0093079bf92815f8bf9be06d32b85c89e0a909))
* **ci:** use versionize --release-as in Bump Version workflow ([#158](https://www.github.com/EntglDb/BLite/issues/158)) ([aea9316](https://www.github.com/EntglDb/BLite/commit/aea931679a16cd444e70619cbcb0dea86dab02b7))
* **core:** invalidate shared free-space index when orphan collections are dropped ([#155](https://www.github.com/EntglDb/BLite/issues/155)) ([beec378](https://www.github.com/EntglDb/BLite/commit/beec3782c5ff1a7d425bf59006ae9df4acc9e04a))
* **index:** exact order-preserving decimal keys for secondary indexes ([#150](https://www.github.com/EntglDb/BLite/issues/150)) ([#157](https://www.github.com/EntglDb/BLite/issues/157)) ([3a51517](https://www.github.com/EntglDb/BLite/commit/3a5151796e06cc165a77a9eb877a9c6b5f5b05ad))
* **index:** fixed root page for B-tree, R-Tree and vector indexes ([#151](https://www.github.com/EntglDb/BLite/issues/151)) ([#156](https://www.github.com/EntglDb/BLite/issues/156)) ([e187bd6](https://www.github.com/EntglDb/BLite/commit/e187bd67ffa88297f12d0bf841b0cb92cc01f2ba))

<a name="5.1.1"></a>
## [5.1.1](https://www.github.com/EntglDb/BLite/releases/tag/v5.1.1) (2026-09-13)

### Bug Fixes

* **query:** fall back to full scan for computed properties in predicates ([#144](https://www.github.com/EntglDb/BLite/issues/144)) ([eaf9594](https://www.github.com/EntglDb/BLite/commit/eaf9594b6ef7a1c8eafaa28275c593119ec7ca31))

<a name="5.1.0"></a>
## [5.1.0](https://www.github.com/EntglDb/BLite/releases/tag/v5.1.0) (2026-09-03)

### Features

* add UpsertAsync/UpsertBulkAsync ([#142](https://www.github.com/EntglDb/BLite/issues/142)) ([dd31ff2](https://www.github.com/EntglDb/BLite/commit/dd31ff2f7cd8b19afaf6d7299940fb2e38ba57e8))

<a name="5.0.9"></a>
## [5.0.9](https://www.github.com/EntglDb/BLite/releases/tag/v5.0.9) (2026-08-27)

<a name="5.0.8"></a>
## [5.0.8](https://www.github.com/EntglDb/BLite/releases/tag/v5.0.8) (2026-08-22)

<a name="5.0.7"></a>
## [5.0.7](https://www.github.com/EntglDb/BLite/releases/tag/v5.0.7) (2026-08-22)

<a name="5.0.6"></a>
## [5.0.6](https://www.github.com/EntglDb/BLite/releases/tag/v5.0.6) (2026-08-22)

<a name="5.0.5"></a>
## [5.0.5](https://www.github.com/EntglDb/BLite/releases/tag/v5.0.5) (2026-08-14)

<a name="5.0.4"></a>
## [5.0.4](https://www.github.com/EntglDb/BLite/releases/tag/v5.0.4) (2026-07-22)

<a name="5.0.3"></a>
## [5.0.3](https://www.github.com/EntglDb/BLite/releases/tag/v5.0.3) (2026-07-09)

<a name="5.0.1"></a>
## [5.0.1](https://www.github.com/EntglDb/BLite/releases/tag/v5.0.1) (2026-06-07)

<a name="5.0.0"></a>
## [5.0.0](https://www.github.com/EntglDb/BLite/releases/tag/v5.0.0) (2026-06-02)

<a name="5.0.0-preview.1"></a>
## [5.0.0-preview.1](https://www.github.com/EntglDb/BLite/releases/tag/v5.0.0-preview.1) (2026-05-11)

<a name="5.0.0-preview.0"></a>
## [5.0.0-preview.0](https://www.github.com/EntglDb/BLite/releases/tag/v5.0.0-preview.0) (2026-05-04)

### Features

* Audit Trail Module — IBLiteAuditSink, BLiteMetrics, DiagnosticSource ([#109](https://www.github.com/EntglDb/BLite/issues/109)) ([d4bff36](https://www.github.com/EntglDb/BLite/commit/d4bff3652dbb35b689a4ed9568f9e1e5175de74b))
* Core Crypto Layer — ICryptoProvider, AesGcmCryptoProvider, PageFile hooks ([#100](https://www.github.com/EntglDb/BLite/issues/100)) ([c2bfec6](https://www.github.com/EntglDb/BLite/commit/c2bfec6b9003b44ef083f1fb4e41a323189f9128))
* Encrypt WAL records with AES-256-GCM (FileRole=3) ([#101](https://www.github.com/EntglDb/BLite/issues/101)) ([f2b8d4a](https://www.github.com/EntglDb/BLite/commit/f2b8d4a55da42747af23b6f2105cba14a0c64cdd))
* EncryptionCoordinator for multi-file mode — per-file HKDF-SHA256 subkey derivation ([#102](https://www.github.com/EntglDb/BLite/issues/102)) ([5e4ca92](https://www.github.com/EntglDb/BLite/commit/5e4ca9219d37586d65fd77610c764200db080d65))
* Generalized Retention Policy for All Collection Types ([#97](https://www.github.com/EntglDb/BLite/issues/97)) ([74094db](https://www.github.com/EntglDb/BLite/commit/74094db0e8d00d626e97c2cf059eb85c5c310f7f))
* implement multi-process WAL via .wal-shm sidecar ([#116](https://www.github.com/EntglDb/BLite/issues/116)) ([60dc01b](https://www.github.com/EntglDb/BLite/commit/60dc01b727783b5d5f0a1442b66cca079d647c75))
* Multi-process WAL phases 4/5/6/7 — SHM hash table, reader slots, bounded checkpoint, incremental replay ([#117](https://www.github.com/EntglDb/BLite/issues/117)) ([7ce44c1](https://www.github.com/EntglDb/BLite/commit/7ce44c1bf5706a61d602e14082abda7cb0662bdc))
* Slot-level secure erase and VACUUM for GDPR Art. 17 compliance ([#96](https://www.github.com/EntglDb/BLite/issues/96)) ([646d940](https://www.github.com/EntglDb/BLite/commit/646d940a1d93aa63d8fd779ae630501a7642c852))
* **gdpr:** WP1 — PersonalData annotation, Subject Export (Art. 15/20), Database Inspection (Art. 30) ([#111](https://www.github.com/EntglDb/BLite/issues/111)) ([dab21b6](https://www.github.com/EntglDb/BLite/commit/dab21b6fd09181d454b24ab44715f1bc087bfca0))
* **gdpr:** WP2 — CDC Field Masking ([#112](https://www.github.com/EntglDb/BLite/issues/112)) ([0d6f3bc](https://www.github.com/EntglDb/BLite/commit/0d6f3bc5aa44788a6aa833426953a416f39cf959))
* **gdpr-wp3:** GdprMode.Strict — privacy-by-default orchestration (Art. 25) ([#113](https://www.github.com/EntglDb/BLite/issues/113)) ([01ad3fa](https://www.github.com/EntglDb/BLite/commit/01ad3fab18f5f3d6993e0f50aa6ca9074f46bb00))

### Bug Fixes

* **studio:** correct dark mode backgrounds in Schema tab ([#115](https://www.github.com/EntglDb/BLite/issues/115)) ([d15e1c0](https://www.github.com/EntglDb/BLite/commit/d15e1c0456bb9850e82b8b1752e8140135fe84e2))

<a name="4.4.2"></a>
## [4.4.2](https://www.github.com/EntglDb/BLite/releases/tag/v4.4.2) (2026-04-25)

<a name="4.4.1"></a>
## [4.4.1](https://www.github.com/EntglDb/BLite/releases/tag/v4.4.1) (2026-04-23)

<a name="4.4.0"></a>
## [4.4.0](https://www.github.com/EntglDb/BLite/releases/tag/v4.4.0) (2026-04-23)

### Features

* add samples/BLite.BlazorWasm counter demo + fix BLite.Wasm TFM ([#64](https://www.github.com/EntglDb/BLite/issues/64)) ([0c4c404](https://www.github.com/EntglDb/BLite/commit/0c4c40404326799a377b9413d514ead97599b278))

<a name="4.3.1"></a>
## [4.3.1](https://www.github.com/EntglDb/BLite/releases/tag/v4.3.1) (2026-04-15)

### Bug Fixes

* correct FSI before throwing "Not enough space" to break poisoned-cache loop ([#59](https://www.github.com/EntglDb/BLite/issues/59)) ([561da42](https://www.github.com/EntglDb/BLite/commit/561da42b4e109fbca23592049dc4576ea7c15539))
* proactive FSI revert on transaction rollback ([#61](https://www.github.com/EntglDb/BLite/issues/61)) ([957629c](https://www.github.com/EntglDb/BLite/commit/957629c41f20cebe598f1302df8bf9059181d31d))

<a name="4.3.0"></a>
## [4.3.0](https://www.github.com/EntglDb/BLite/releases/tag/v4.3.0) (2026-04-11)

<a name="4.3.0-rc.0"></a>
## [4.3.0-rc.0](https://www.github.com/EntglDb/BLite/releases/tag/v4.3.0-rc.0) (2026-04-11)

<a name="4.3.0-preview.0"></a>
## [4.3.0-preview.0](https://www.github.com/EntglDb/BLite/releases/tag/v4.3.0-preview.0) (2026-04-10)

### Features

* Make transactions caller-owned; add metrics ([f42b3e6](https://www.github.com/EntglDb/BLite/commit/f42b3e6624725dd748a5f79a708656015c1cf6e3))

<a name="4.2.1-preview.3"></a>
## [4.2.1-preview.3](https://www.github.com/EntglDb/BLite/releases/tag/v4.2.1-preview.3) (2026-04-09)

<a name="4.2.1-preview.2"></a>
## [4.2.1-preview.2](https://www.github.com/EntglDb/BLite/releases/tag/v4.2.1-preview.2) (2026-04-09)

<a name="4.2.1-preview.1"></a>
## [4.2.1-preview.1](https://www.github.com/EntglDb/BLite/releases/tag/v4.2.1-preview.1) (2026-04-09)

<a name="4.2.1-preview.0"></a>
## [4.2.1-preview.0](https://www.github.com/EntglDb/BLite/releases/tag/v4.2.1-preview.0) (2026-04-08)

<a name="4.2.0"></a>
## [4.2.0](https://www.github.com/EntglDb/BLite/releases/tag/v4.2.0) (2026-04-07)

### Features

* add IDocumentDbContext interface and make DocumentDbContext implement it ([#36](https://www.github.com/EntglDb/BLite/issues/36)) ([8f50a57](https://www.github.com/EntglDb/BLite/commit/8f50a578087f29bebd9f07dcc008b6911a2dd2cf))
* non-indexed query optimization — BSON-level scan for count, OLAP, and wider predicate coverage ([#46](https://www.github.com/EntglDb/BLite/issues/46)) ([b1c20d0](https://www.github.com/EntglDb/BLite/commit/b1c20d0cb0c9d6d82fb90a36a7aac8d8beac4ffc))

<a name="4.1.6"></a>
## [4.1.6](https://www.github.com/EntglDb/BLite/releases/tag/v4.1.6) (2026-04-05)

### Bug Fixes

* Handle null strings and byte[] in source generator ([fbf5d58](https://www.github.com/EntglDb/BLite/commit/fbf5d5862f7136a3ad33344dc4f44f0882850440))

<a name="4.1.5"></a>
## [4.1.5](https://www.github.com/EntglDb/BLite/releases/tag/v4.1.5) (2026-04-05)

<a name="4.1.4"></a>
## [4.1.4](https://www.github.com/EntglDb/BLite/releases/tag/v4.1.4) (2026-04-04)

<a name="4.1.3"></a>
## [4.1.3](https://www.github.com/EntglDb/BLite/releases/tag/v4.1.3) (2026-04-04)

<a name="4.1.2"></a>
## [4.1.2](https://www.github.com/EntglDb/BLite/releases/tag/v4.1.2) (2026-04-03)

<a name="4.1.1"></a>
## [4.1.1](https://www.github.com/EntglDb/BLite/releases/tag/v4.1.1) (2026-04-02)

### Bug Fixes

* Query, index and transaction optimizations ([e7583cb](https://www.github.com/EntglDb/BLite/commit/e7583cbb64dc848a28443e1abba782dd2f5f1077))

<a name="4.1.0"></a>
## [4.1.0](https://www.github.com/EntglDb/BLite/releases/tag/v4.1.0) (2026-04-01)

### Features

* complete async LINQ materialiser layer for IBLiteQueryable ([#30](https://www.github.com/EntglDb/BLite/issues/30)) ([2499054](https://www.github.com/EntglDb/BLite/commit/2499054432c66070b06080a99495028951e3c4ed))

### Bug Fixes

* Add warm-start buffer sizing; remove WriteCString ([90955bd](https://www.github.com/EntglDb/BLite/commit/90955bd20350332957ad32bf980f3bfcf15e71ab))

<a name="4.0.3"></a>
## [4.0.3](https://www.github.com/EntglDb/BLite/releases/tag/v4.0.3) (2026-03-31)

<a name="4.0.2"></a>
## [4.0.2](https://www.github.com/EntglDb/BLite/releases/tag/v4.0.2) (2026-03-30)

<a name="4.0.1"></a>
## [4.0.1](https://www.github.com/EntglDb/BLite/releases/tag/v4.0.1) (2026-03-26)

### Bug Fixes

* Support ObjectId ordering, fix Execute deadlock ([609cb8a](https://www.github.com/EntglDb/BLite/commit/609cb8a1163e1dc88becf57a64713c1ed4a19ddc))

<a name="4.0.0"></a>
## [4.0.0](https://www.github.com/EntglDb/BLite/releases/tag/v4.0.0) (2026-03-25)

### Features

* Document async-only CRUD API & update samples ([e13fe24](https://www.github.com/EntglDb/BLite/commit/e13fe24e7888ea9067b9423422b682d851111f4d))

### Breaking Changes

* Document async-only CRUD API & update samples ([e13fe24](https://www.github.com/EntglDb/BLite/commit/e13fe24e7888ea9067b9423422b682d851111f4d))

<a name="3.8.1-preview.3"></a>
## [3.8.1-preview.3](https://www.github.com/EntglDb/BLite/releases/tag/v3.8.1-preview.3) (2026-03-25)

<a name="3.8.1-preview.2"></a>
## [3.8.1-preview.2](https://www.github.com/EntglDb/BLite/releases/tag/v3.8.1-preview.2) (2026-03-25)

<a name="3.8.1-preview.1"></a>
## [3.8.1-preview.1](https://www.github.com/EntglDb/BLite/releases/tag/v3.8.1-preview.1) (2026-03-25)

<a name="3.8.1-preview.0"></a>
## [3.8.1-preview.0](https://www.github.com/EntglDb/BLite/releases/tag/v3.8.1-preview.0) (2026-03-24)

<a name="3.8.0"></a>
## [3.8.0](https://www.github.com/EntglDb/BLite/releases/tag/v3.8.0) (2026-03-23)

<a name="3.8.0-preview.3"></a>
## [3.8.0-preview.3](https://www.github.com/EntglDb/BLite/releases/tag/v3.8.0-preview.3) (2026-03-22)

### Bug Fixes

* Non-blocking checkpoints; defer metadata writes ([455d28a](https://www.github.com/EntglDb/BLite/commit/455d28af28df3ebd5459e07e4d208b15d6061e89))
* Use Lazy<PageFile> for collection files ([8e017a1](https://www.github.com/EntglDb/BLite/commit/8e017a128be72b3533aed61eee98bb2a7a7b9c37))

<a name="3.8.0-preview.2"></a>
## [3.8.0-preview.2](https://www.github.com/EntglDb/BLite/releases/tag/v3.8.0-preview.2) (2026-03-22)

### Features

* Add session APIs, metadata lock, and benchmarks ([7e7e3a0](https://www.github.com/EntglDb/BLite/commit/7e7e3a09e8eaa4477ac9689598c3a06c75663472))

<a name="3.8.0-preview.1"></a>
## [3.8.0-preview.1](https://www.github.com/EntglDb/BLite/releases/tag/v3.8.0-preview.1) (2026-03-22)

<a name="3.8.0-preview.0"></a>
## [3.8.0-preview.0](https://www.github.com/EntglDb/BLite/releases/tag/v3.8.0-preview.0) (2026-03-22)

### Features

* add BLiteSession for per-connection isolated transaction context (server mode) ([13b59ec](https://www.github.com/EntglDb/BLite/commit/13b59ec0e72302fad08771bdef49e050c77f661c))
* **storage:** Server() base config, BLiteMigration (single↔multi), KvGetWithExpiry + ImportDictionary ([1a7946e](https://www.github.com/EntglDb/BLite/commit/1a7946eae2ab43b89e98809bcd87992c58d4a950))

### Bug Fixes

* harden PageFile post-dispose safety — volatile _disposed, ThrowIfDisposed, null fields on close ([3411e17](https://www.github.com/EntglDb/BLite/commit/3411e179da5783cbccd2a6812b8749697d22271d))
* replace SemaphoreSlim with ReaderWriterLockSlim in PageFile to fix concurrent read/write race ([97dbb47](https://www.github.com/EntglDb/BLite/commit/97dbb477b274666c9194806bd384c61b11e9624f))
* use _rwLock read lock for netstandard2.1 ReadPageAsync to prevent race with EnsureCapacityCore ([815cfa3](https://www.github.com/EntglDb/BLite/commit/815cfa3cfd002d4f7c484b9b0ae8dad28a34abe7))
* **storage:** bit-tagged pageIds for collision-free multi-file routing, collection scans, WAL recovery, path validation ([7b643a5](https://www.github.com/EntglDb/BLite/commit/7b643a5eccb0ff89000ffda3c45731e183edc572))
* **storage:** implement bit-tagged pageIds to eliminate collision and routing bugs ([4c8ef15](https://www.github.com/EntglDb/BLite/commit/4c8ef15496d3309c3ee991ae10b61be57a14c948))

<a name="3.7.0"></a>
## [3.7.0](https://www.github.com/EntglDb/BLite/releases/tag/v3.7.0) (2026-03-17)

### Features

* optimizing OLAP queries ([f83e0d5](https://www.github.com/EntglDb/BLite/commit/f83e0d561eb875248bcb55c97c10f2ec4839a659))
* optmized group by OLAP queries ([98c49ef](https://www.github.com/EntglDb/BLite/commit/98c49ef9716d55c9cb52663e42677f45dedb32b9))

<a name="3.6.9"></a>
## [3.6.9](https://www.github.com/EntglDb/BLite/releases/tag/v3.6.9) (2026-03-16)

### Bug Fixes

* **sourcegen:** fix mapper name collisions when entity types share a simple class name across different namespaces — underscore characters in namespace/type names are now escaped before separator replacement, making the `GetMapperName` transform bijective ([ee67c33](https://www.github.com/EntglDb/BLite/commit/ee67c33))
* **sourcegen:** parse `.ToCollection("name")` from `OnModelCreating` fluent chains (including `const string` and other compile-time constants) so duplicate-named entities in different namespaces use distinct database collections ([ee67c33](https://www.github.com/EntglDb/BLite/commit/ee67c33))
* **sourcegen:** emit BLITE003 build-time error when two or more entity types in the same `DocumentDbContext` resolve to the same collection name (case-insensitive), preventing silent data corruption at runtime ([ee67c33](https://www.github.com/EntglDb/BLite/commit/ee67c33))

<a name="3.6.8"></a>
## [3.6.8](https://www.github.com/EntglDb/BLite/releases/tag/v3.6.8) (2026-03-16)

<a name="3.6.7"></a>
## [3.6.7](https://www.github.com/EntglDb/BLite/releases/tag/v3.6.7) (2026-03-16)

### Bug Fixes

* **sourcegen:** increase nested type traversal depth to 20 and emit BLITE001 errors instead of silent skips ([fea19e4](https://www.github.com/EntglDb/BLite/commit/fea19e434748bf5fef95f81410f25b036ab64cf2))

<a name="3.6.6"></a>
## [3.6.6](https://www.github.com/EntglDb/BLite/releases/tag/v3.6.6) (2026-03-15)

### Bug Fixes

* AddString EnsureCapacity uses actual UTF-8 byte count ([b5c55be](https://www.github.com/EntglDb/BLite/commit/b5c55bea0120ea77b65087a53b24326dbd8890e4))

<a name="3.6.5"></a>
## [3.6.5](https://www.github.com/EntglDb/BLite/releases/tag/v3.6.5) (2026-03-14)

<a name="3.6.4"></a>
## [3.6.4](https://www.github.com/EntglDb/BLite/releases/tag/v3.6.4) (2026-03-13)

<a name="3.6.3"></a>
## [3.6.3](https://www.github.com/EntglDb/BLite/releases/tag/v3.6.3) (2026-03-12)

### Bug Fixes

* coerced BSON numeric reads + JSON type-preservation ([38d77df](https://www.github.com/EntglDb/BLite/commit/38d77df3095d6a52690277001fa98c428942b38c))

<a name="3.6.2"></a>
## [3.6.2](https://www.github.com/EntglDb/BLite/releases/tag/v3.6.2) (2026-03-12)

### Bug Fixes

* HNSW vector search correctness + edge-case tests ([fca8fc5](https://www.github.com/EntglDb/BLite/commit/fca8fc521e26a2f8947233a682205fde3f46e7e9))

<a name="3.6.1"></a>
## [3.6.1](https://www.github.com/EntglDb/BLite/releases/tag/v3.6.1) (2026-03-11)

### Bug Fixes

* corrects duplicates index inserts ([8b99e9c](https://www.github.com/EntglDb/BLite/commit/8b99e9c5c79a77565695219e528a981eeedf6610))

<a name="3.6.0"></a>
## [3.6.0](https://www.github.com/EntglDb/BLite/releases/tag/v3.6.0) (2026-03-11)

### Features

* adds watch to dynamic collections ([d22857f](https://www.github.com/EntglDb/BLite/commit/d22857f0d5f31818362aa469ed20a8f0e4557336))

<a name="3.5.2"></a>
## [3.5.2](https://www.github.com/EntglDb/BLite/releases/tag/v3.5.2) (2026-03-11)

### Bug Fixes

* adds abstractions for indexes ([59874a7](https://www.github.com/EntglDb/BLite/commit/59874a76ffdd039bd966ac247281da2cce428110))

<a name="3.5.1"></a>
## [3.5.1](https://www.github.com/EntglDb/BLite/releases/tag/v3.5.1) (2026-03-11)

### Bug Fixes

* add missing sync Update, UpdateBulk, Delete, DeleteBulk to IDocumentCollection ([d719b6d](https://www.github.com/EntglDb/BLite/commit/d719b6dcc21b25e03048fe9df96c122f99e17368))

<a name="3.5.0"></a>
## [3.5.0](https://www.github.com/EntglDb/BLite/releases/tag/v3.5.0) (2026-03-11)

### Features

* introduce IDocumentCollection<TId,T> abstraction ([fa68e0a](https://www.github.com/EntglDb/BLite/commit/fa68e0a0f54e8da1fc3cf99cb8d1a85daae690ff))

<a name="3.4.2"></a>
## [3.4.2](https://www.github.com/EntglDb/BLite/releases/tag/v3.4.2) (2026-03-10)

### Bug Fixes

* delete collection metadata ([19dac17](https://www.github.com/EntglDb/BLite/commit/19dac17dff255f59c350d0f0625ca52a686b0337))

<a name="3.4.1"></a>
## [3.4.1](https://www.github.com/EntglDb/BLite/releases/tag/v3.4.1) (2026-03-10)

<a name="3.4.0"></a>
## [3.4.0](https://www.github.com/EntglDb/BLite/releases/tag/v3.4.0) (2026-03-10)

### Features

* auto id fallback for string and Guid ([58a99ec](https://www.github.com/EntglDb/BLite/commit/58a99ec0c86ec453ff795d6258822a5495c98253))

### Bug Fixes

* corrects index navigation for number based indexes ([685873e](https://www.github.com/EntglDb/BLite/commit/685873e8050d7d0f0db2914142de2391ab553797))

<a name="3.3.0"></a>
## [3.3.0](https://www.github.com/EntglDb/BLite/releases/tag/v3.3.0) (2026-03-09)

### Features

* page compaction on delete, private-setter entity tests, TimeSeries in DocumentDbContext ([63213be](https://www.github.com/EntglDb/BLite/commit/63213bec4ad8e47cdf3014d9408b939f8a38352a))
* **studio:** adds better json view ([9991c67](https://www.github.com/EntglDb/BLite/commit/9991c677f3d3e851e02edb01dbc20fdc9ba85c22))

<a name="3.1.1"></a>
## [3.1.1](https://www.github.com/EntglDb/BLite/releases/tag/v3.1.1) (2026-03-07)

### Features

* **sourcegen:** supporto DDD private backing field per collezioni ([46f4e9a](https://www.github.com/EntglDb/BLite/commit/46f4e9a570a0d80aad7a0701b209724f9bce0bb0))

### Bug Fixes

* IndexOptimizer regressione con chiavi ValueObject senza registry ([50c5938](https://www.github.com/EntglDb/BLite/commit/50c5938dcbc41be36b2059c833c23aa83a532e96))

<a name="3.1.0"></a>
## [3.1.0](https://www.github.com/EntglDb/BLite/releases/tag/v3.1.0) (2026-03-07)

### Features

* integrate ValueConverter registry into query pipeline ([4de5919](https://www.github.com/EntglDb/BLite/commit/4de5919ff1cfbfcf4b0b55d247765db42f7a4588))

### Bug Fixes

* **query:** support AndAlso, .Equals() calls and closure captures in expression analysis ([715a77d](https://www.github.com/EntglDb/BLite/commit/715a77d10e5e36b46168babe3714e5a2fc2ef13f))

<a name="3.0.1"></a>
## [3.0.1](https://www.github.com/EntglDb/BLite/releases/tag/v3.0.1) (2026-03-07)

### Bug Fixes

* **schema:** prevent buffer overflow when serializing schemas larger than PageSize ([2495d20](https://www.github.com/EntglDb/BLite/commit/2495d20d4449e3c6028d386099e3b522f3bb862d))

<a name="3.0.0"></a>
## [3.0.0](https://www.github.com/EntglDb/BLite/releases/tag/v3.0.0) (2026-03-06)

### Features

* Add support for embedded property indexes and self-referencing collections ([fd5f9e8](https://www.github.com/EntglDb/BLite/commit/fd5f9e8b4ef46fede0925e84719890dabcc3fc08))

### Breaking Changes

* clarify ModelBuilder configuration precedence and tie HasConversion to Property() ([a5827a5](https://www.github.com/EntglDb/BLite/commit/a5827a50d690d307e96b2941d2c45f5a46f63d87))

<a name="2.0.2"></a>
## [2.0.2](https://www.github.com/EntglDb/BLite/releases/tag/v2.0.2) (2026-03-05)

### Bug Fixes

* corrects missing mapper creation for ValueObjects ([cfde540](https://www.github.com/EntglDb/BLite/commit/cfde540ac4467e5f753481ff09cd367bb7a877b8))

<a name="2.0.1"></a>
## [2.0.1](https://www.github.com/EntglDb/BLite/releases/tag/v2.0.1) (2026-03-04)

### Bug Fixes

* adds all Async methods to DocumentCollection ([954d2cd](https://www.github.com/EntglDb/BLite/commit/954d2cd7ddd3c2555db069aee887ad3d23762a46))

<a name="2.0.0"></a>
## [2.0.0](https://www.github.com/EntglDb/BLite/releases/tag/v2.0.0) (2026-03-02)

### Features

* add netstandard2.1 multi-targeting ([7199025](https://www.github.com/EntglDb/BLite/commit/7199025e9b4f013deae2d4df366f61078c82705c))

### Breaking Changes

* add netstandard2.1 multi-targeting ([7199025](https://www.github.com/EntglDb/BLite/commit/7199025e9b4f013deae2d4df366f61078c82705c))

<a name="1.12.0"></a>
## [1.12.0](https://www.github.com/EntglDb/BLite/releases/tag/v1.12.0) (2026-02-26)

### Features

* native TimeSeries page type with retention pruning + Studio UI ([a119e9b](https://www.github.com/EntglDb/BLite/commit/a119e9b37010c67877765937c5d135de40ba3a5d))

<a name="1.11.0"></a>
## [1.11.0](https://www.github.com/EntglDb/BLite/releases/tag/v1.11.0) (2026-02-25)

### Features

* adds vector source to collection metadata ([5905f20](https://www.github.com/EntglDb/BLite/commit/5905f20414cf0cefac4ae8c93a523b2ab416de58))

### Bug Fixes

* corrects the missing CDC in BLite Engine ([322542a](https://www.github.com/EntglDb/BLite/commit/322542a3db10e1b482ae4b3cc35d2662f8ba33f0))

<a name="1.10.0"></a>
## [1.10.0](https://www.github.com/EntglDb/BLite/releases/tag/v1.10.0) (2026-02-24)

### Features

* advanced BLQL filters ([c9b8fb7](https://www.github.com/EntglDb/BLite/commit/c9b8fb72cc3015ffcabe0cff4c618077fb633024))
* **studio:** add macOS build job — osx-x64 + osx-arm64 .app bundle + .dmg ([224a770](https://www.github.com/EntglDb/BLite/commit/224a770730b26f5bc5d78b70ca81d00ff8e022e1))
* **website:** add macOS download links — Studio page + footer ([c5ddf99](https://www.github.com/EntglDb/BLite/commit/c5ddf99f6c7cf98d7fd79618b3546c136e8afdb2))

### Bug Fixes

* corrects windows BLite Studio pipeline ([07a0266](https://www.github.com/EntglDb/BLite/commit/07a026671adac4bf810b2590fe3b6095d87518a2))
* **studio:** absolute SourceDir path + WixUI_FeatureTree with optional shortcuts and launch checkbox ([f3e56e8](https://www.github.com/EntglDb/BLite/commit/f3e56e828077438e3eb5cf543b4714427e6f3339))
* **studio:** correct WiX 4 Files syntax inside Component ([5442c60](https://www.github.com/EntglDb/BLite/commit/5442c60794f23c54172d06cc7b9c770b81d8d4d2))
* **studio:** fix WiX 4 Files harvesting inside Directory instead of ComponentGroup ([614a83b](https://www.github.com/EntglDb/BLite/commit/614a83baa171a781c549fdfc9dba63fe0d538cd0))
* **studio:** WiX SetProperty launch + LicenseRtf absolute path ([ef598e4](https://www.github.com/EntglDb/BLite/commit/ef598e465bc968d3cbe29a2aba44aaf2206f1296))

<a name="1.9.0"></a>
## [1.9.0](https://www.github.com/EntglDb/BLite/releases/tag/v1.9.0) (2026-02-24)

### Features

* BLite Studio ([11c6eb2](https://www.github.com/EntglDb/BLite/commit/11c6eb200e61154cdf23715584c89a968d507949))
* BLQL BLite Query Language ([a86d458](https://www.github.com/EntglDb/BLite/commit/a86d4584cf87136ed00876d16512a85a2367a213))

<a name="1.8.0"></a>
## [1.8.0](https://www.github.com/EntglDb/BLite/releases/tag/v1.8.0) (2026-02-24)

### Features

* **storage:** add hot backup support (BackupAsync) ([8a608bc](https://www.github.com/EntglDb/BLite/commit/8a608bc4104e11a4954d5b0339cee19841ff1c28))

### Bug Fixes

* ListCollections() now includes persisted collections (warm-up from storage catalog) ([64f7abb](https://www.github.com/EntglDb/BLite/commit/64f7abb32cda7694cf2354fcc177cbfc43902ce9))

<a name="1.7.1"></a>
## [1.7.1](https://www.github.com/EntglDb/BLite/releases/tag/v1.7.1) (2026-02-23)

### Bug Fixes

* corrects array document write to avoid key dictionary lookup ([7ac36a7](https://www.github.com/EntglDb/BLite/commit/7ac36a76c99b85b786f7eec744415cbc6dfdb509))

<a name="1.7.0"></a>
## [1.7.0](https://www.github.com/EntglDb/BLite/releases/tag/v1.7.0) (2026-02-23)

### Features

* **bson:** BsonJsonConverter - JSON<->BSON conversion in BLite.Bson ([c2e295c](https://www.github.com/EntglDb/BLite/commit/c2e295c60149feaad2cfb95a009a34b45f2ec4a2))

### Bug Fixes

* nested objects with [Key] Id + enum index support ([e6a334a](https://www.github.com/EntglDb/BLite/commit/e6a334a6d4bc155adb69e3d340f93ba39c2cc95a))

<a name="1.6.2"></a>
## [1.6.2](https://www.github.com/EntglDb/BLite/releases/tag/v1.6.2) (2026-02-23)

### Bug Fixes

* allows document metadata page overflow ([c663462](https://www.github.com/EntglDb/BLite/commit/c663462cdaf25868282678103a349ad07fb5487d))

<a name="1.6.1"></a>
## [1.6.1](https://www.github.com/EntglDb/BLite/releases/tag/v1.6.1) (2026-02-22)

### Bug Fixes

* add enum support to Source Generator serialization ([7096950](https://www.github.com/EntglDb/BLite/commit/709695028aeb9633be14038b93626dfcfcc8756d))

<a name="1.6.0"></a>
## [1.6.0](https://www.github.com/EntglDb/BLite/releases/tag/v1.6.0) (2026-02-22)

### Features

* expose RegisterKeys/GetKeyMap publicly on BLiteEngine ([cb9c6c9](https://www.github.com/EntglDb/BLite/commit/cb9c6c9703abc1608beb387d45f9fb60a2cc7cda))

<a name="1.5.0"></a>
## [1.5.0](https://www.github.com/EntglDb/BLite/releases/tag/v1.5.0) (2026-02-22)

### Features

* add FindAsync(predicate, ct) to DocumentCollection ([ee9566a](https://www.github.com/EntglDb/BLite/commit/ee9566a4f286ad47e53d4c41b2b8b88d1d959cb6))
* extend push-down to WHERE+SELECT combined queries ([ea4feaf](https://www.github.com/EntglDb/BLite/commit/ea4feaf9a6950f340ef19d240f221e4c1f91f59c))
* IBLiteQueryable<T> + async LINQ chain preservation (Step 1) ([c1926bb](https://www.github.com/EntglDb/BLite/commit/c1926bbe5f9e6a2162c98fa5e662343bfa871469))
* push-down SELECT projection at storage level (Steps 2-5) ([df7cceb](https://www.github.com/EntglDb/BLite/commit/df7ccebee1904369c552d26679f92c59dea7a2ba))
* symmetric CRUD API for DynamicCollection and BLiteEngine ([42cb68c](https://www.github.com/EntglDb/BLite/commit/42cb68cbeae7dec02b7ee2ffbf949eb05d6059da))

### Bug Fixes

* restore IAsyncEnumerable<T> on BTreeQueryable<T> ([444a96a](https://www.github.com/EntglDb/BLite/commit/444a96a6db043e9bc8a60b6e6b5278fbda4e3dba))

<a name="1.4.0"></a>
## [1.4.0](https://www.github.com/EntglDb/BLite/releases/tag/v1.4.0) (2026-02-21)

### Features

* async read infrastructure + BTreeIndex async API + concurrency fix ([0636221](https://www.github.com/EntglDb/BLite/commit/063622108a4af7d4ccd1499d47b20abf35a5311b))
* async read path for DocumentCollection, DynamicCollection, BLiteEngine and AsQueryable ([71266a7](https://www.github.com/EntglDb/BLite/commit/71266a7aeab8c595a7a9a8fb1f44606c3001084a))
* BLiteEngine for generic access to data ([39e6c89](https://www.github.com/EntglDb/BLite/commit/39e6c89971597700e585b7e3d2ea47b0e3a1a692))

<a name="1.3.1"></a>
## [1.3.1](https://www.github.com/EntglDb/BLite/releases/tag/v1.3.1) (2026-02-19)

### Bug Fixes

* generate SerializeFields and Deserialize(ref) for all entities ([69555cd](https://www.github.com/EntglDb/BLite/commit/69555cd16e11f0bf69b73b503ee3bb8e6511f9b5))

<a name="1.3.0"></a>
## [1.3.0](https://www.github.com/EntglDb/BLite/releases/tag/v1.3.0) (2026-02-19)

### Features

* **bson:** add support for DateTimeOffset, TimeSpan, DateOnly and TimeOnly ([43f92a9](https://www.github.com/EntglDb/BLite/commit/43f92a9f0954135ebeab276eabb1668f20cdc399))

<a name="1.2.1"></a>
## [1.2.1](https://www.github.com/EntglDb/BLite/releases/tag/v1.2.1) (2026-02-19)

### Bug Fixes

* **source-generator:** handle nullable string Id in mapper base class selection ([a12bb94](https://www.github.com/EntglDb/BLite/commit/a12bb94c3a99c76d52ed3277e7dec6c05d373477))

<a name="1.2.0"></a>
## [1.2.0](https://www.github.com/EntglDb/BLite/releases/tag/v1.2.0) (2026-02-19)

### Features

* **source-generator:** add circular reference and N-N relationship tests ([d48c8fc](https://www.github.com/EntglDb/BLite/commit/d48c8fc471c9428fdf9b44ce8f8cf194d1c4dbfd))
* **source-generator:** add private setter/constructor support via Expression Trees ([3d017ba](https://www.github.com/EntglDb/BLite/commit/3d017ba9d8335e8f3ba103872e3740423a3dc414))
* **source-generator:** improve property and collection analysis ([9506d1f](https://www.github.com/EntglDb/BLite/commit/9506d1fb2a76e3f563ca930503fde3365e98d399))

<a name="1.1.1"></a>
## [1.1.1](https://www.github.com/EntglDb/BLite/releases/tag/v1.1.1) (2026-02-18)

### Bug Fixes

* inheritance of dbcontext for set method ([dd59f8f](https://www.github.com/EntglDb/BLite/commit/dd59f8f621df4874709d0dd8fda91e895ab01920))

<a name="1.1.0"></a>
## [1.1.0](https://www.github.com/EntglDb/BLite/releases/tag/v1.1.0) (2026-02-18)

### Features

* adds Set<TId, T> to DocumentDbContext ([f3a6b5d](https://www.github.com/EntglDb/BLite/commit/f3a6b5d645cc1f5fb0d0526307a81cd730d3fb4e))

<a name="1.0.5"></a>
## [1.0.5](https://www.github.com/EntglDb/BLite/releases/tag/v1.0.5) (2026-02-17)

<a name="1.0.4"></a>
## [1.0.4](https://www.github.com/EntglDb/BLite/releases/tag/v1.0.4) (2026-02-17)

### Bug Fixes

* **source-generators:** correct ref struct handling for nested object serialization ([079d8c0](https://www.github.com/EntglDb/BLite/commit/079d8c0a5acb6f24820fc0f01f4be2c8fe6c8bb6))

<a name="1.0.3"></a>
## [1.0.3](https://www.github.com/EntglDb/BLite/releases/tag/v1.0.3) (2026-02-17)

### Bug Fixes

* add SemaphoreSlim to protect CurrentTransaction from race conditions ([f409e36](https://www.github.com/EntglDb/BLite/commit/f409e36ad43c9cc5291504d4ce990cd84afe80da))
* corrects mapper generation for nested objects and collections ([3382fc4](https://www.github.com/EntglDb/BLite/commit/3382fc466e1ab8dabb83851ca1cdf21ae7861b4a))
* handle cancellation token properly in BeginTransactionAsync ([394bf85](https://www.github.com/EntglDb/BLite/commit/394bf8552fe60648481b7ce30e8d02334a5b9ea5))

<a name="1.0.2"></a>
## [1.0.2](https://www.github.com/EntglDb/BLite/releases/tag/v1.0.2) (2026-02-16)

### Bug Fixes

* corrects nuget publish info ([d73016a](https://www.github.com/EntglDb/BLite/commit/d73016a3699af84067f820bb0b1e5b59f9b26edc))


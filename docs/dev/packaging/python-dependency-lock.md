# Python依存lock

- Task: `PK-001`
- Evidence date: 2026-08-30
- Target: Windows x64 / CPython 3.12
- Release approval: **not granted**

## 固定範囲

製品workerのPython依存は次の3成果物を正本とする。

| 成果物 | 範囲 |
|---|---|
| `src/HubRadio.Worker/requirements.lock` | `faster-whisper`、CTranslate2と全runtime transitive wheel 25件 |
| `src/HubRadio.Worker/requirements-test.lock` | runtime lockをincludeし、pytestとtest固有transitive wheel 4件を追加 |
| `src/HubRadio.Worker/wheel-matrix.json` | 29 wheelのfile name、byte size、SHA-256、target、採否、残gate |

`pyproject.toml`はCPython 3.12系だけを許可し、直接runtime依存、test依存、setuptools build backendをexact versionで宣言する。lockは`--require-hashes`と`--only-binary=:all:`を必須にし、sdist buildやresolverによるsilent updateを許可しない。canonical `build.ps1`はtest overlayから全29 wheelを復元した後、local workerだけを`--no-deps --no-build-isolation`でeditable installし、`pip check`を実行する。build時にlockを再生成しない。

## 実測したartifact集合

構成済みMicrosoft package feed proxyに対し、cacheを使わずbinary限定でWindows x64 / CPython 3.12の依存を解決した。実測interpreterはCPython 3.12.10、pipは25.0.1で、29 wheelのartifact-set SHA-256は`5f52dce86a3c0b4fcf198e452123e345afd4429f7c5a354e4b1d1b6ed6cdbe6e`である。集約式はmatrixへ固定した。

`onnxruntime`、PyAV、NumPy、Hugging Face Hub、tokenizers等は、現在の`faster-whisper==1.2.1`またはCTranslate2のpackage metadataが要求するためruntime lockへ含む。機能上`vad_filter=False`でもdependency graphから独断で削除しない。pytestだけが要求するpackageはtest overlayへ分離する。

## 再現確認

hash lockの受入では、既存worker venvを再利用せず、同一Windows x64 hostの空の一時directoryにCPython 3.12 venvを作る。次を満たす場合だけPK-001のfresh-environment証拠とする。

1. lockを`--require-hashes --only-binary=:all:`でdownloadできる。
2. download済みwheelhouseだけを使う`--no-index` installが成功する。
3. local workerをdependency解決なし・build isolationなしでinstallできる。
4. `pip check`、全locked distributionのversion照合、worker testが成功する。
5. wheelhouseの29 fileとmatrixのfile name、byte size、SHA-256、集約hashが一致する。

2026年8月30日に、既存worker venvを使わない一時CPython 3.12.10 AMD64環境で上記を実行した。29 wheelをnetworkなしでinstallし、locked distribution 29件、`pip check`、worker test 80/80、wheelhouse 29/29の個別size/hash照合がすべて成功した。canonical Release buildでもhash lock復元、no-index local install、dependency consistency、Web/.NET/Python static gateが成功した。

この確認はfresh virtual environmentの再現性であり、別のclean Windows machine、MSIX、offline runtime bundle、install/upgrade/uninstall、release readinessの証拠ではない。

## 除外と残gate

- 採用実行経路は`faster-whisper` small / CPU / int8だけである。
- A-010のpyannoteとA-015のlocal text modelはcondition-false、A-012のshot detectorとA-013のface detectorはheldであり、lockへ含めない。
- `faster-whisper` conversion extra、Torch、Transformers、Foundry Local CUDA cache、CUDA runtime bundleは含めない。観測済みCUDA cacheの再配布承認はfalseのままである。
- Python runtime本体のexact artifact/hash/noticeはPK-002、ASR modelのoffline packageはPK-004、clean target machineと完全packageはPK-006、transitive license/CVE/SBOM closureはQ-010で扱う。

したがってPK-001は採用wheel集合の再現を固定するが、model、Python runtime、CUDA、完全notice、releaseを承認しない。
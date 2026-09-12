using System.IO.Compression;

namespace SkyrimJPStringPatcherGui.Services;

/// <summary>
/// v0.55.0 (既知の課題19.): 「翻訳状況を初期化」「選択プラグインを一括初期化」
/// 「MO2再読込＆初期化」はいずれも対象プラグインのTranslation/out_temp配下を
/// 破壊的に書き戻す（--discard-user-edits）——⑤⑥の生成AI・ローカルLLM翻訳結果や
/// 詳細確認ウィンドウでの手動編集も道連れに消える。この破壊の直前に、対象
/// プラグインのout_tempサブフォルダを丸ごと Translation/bak/&lt;タイムスタンプ&gt;.zip
/// へまとめておくことで、誤操作・想定外の初期化から復旧できるようにする。
///
/// 3つの操作（1件/選択複数件/全件）で「対象プラグインの数」が違うだけで、
/// バックアップの構造自体は常に同じにする——1件だけの操作でもZIPを作る
/// （参照の仕方が操作によって変わると使いにくいため）。コピー対象を絞る
/// 特別なロジックは持たず、プラグインフォルダの中身（translations.tsv・
/// prompt.txt等）を丸ごと格納する——除外する積極的な理由がなく、個別の
/// ファイル名を列挙するより単純なため。
///
/// 圧縮するのは、全件対象（MO2再読込＆初期化）だとテキストファイルとはいえ
/// 累積サイズがそこそこ大きくなる（実測で176プラグイン分・11MB）ため。
/// 世代数の上限は設けない——対象はテキストファイル中心で圧縮後は更に小さく、
/// 自動削除はむしろ「うっかり消さないための機能」という目的と矛盾しかねない。
///
/// 保存先は既存の.gitignoreでderivativeフォルダとして予約済みのTranslation/bak/
/// を再利用する（現状コード上は未使用）。
///
/// 2026-09-12: Interface翻訳側（InterfaceText/Translation/out_temp・
/// InterfaceText/Translation/bak）と共有できるよう、out_tempフォルダ・
/// タイムスタンプ判定用ファイル名を引数化した——このクラス自体はCore/
/// Translationへの参照を持たない純粋なファイルI/Oユーティリティで、
/// ESP固有のロジックは無いため、コピーではなく汎用化して両方から呼ぶ。
/// </summary>
public static class TranslationBackup
{
    /// <summary>Zips each named subfolder of <paramref name="outTempDir"/>
    /// (whole contents) into a new "bak/&lt;timestamp&gt;.zip" sibling of
    /// <paramref name="outTempDir"/>, before the caller performs a destructive
    /// re-init on it. A name with no existing subfolder yet (never scanned/
    /// translated) is silently skipped — there is nothing to lose for it.
    /// No-op if nothing exists to back up.</summary>
    /// <param name="outTempDir">The out_temp folder whose subfolders (one per
    /// plugin/mod) are the backup source — e.g. "Translation/out_temp" or
    /// "InterfaceText/Translation/out_temp". The backup zip is written to
    /// this folder's own parent, under "bak/".</param>
    /// <param name="folderNames">Subfolder names under <paramref
    /// name="outTempDir"/> to back up (plugin folder names, or mod names).</param>
    /// <param name="timestampSourceFileName">The per-folder file whose
    /// LastWriteTime decides the backup's timestamp (see remarks below) —
    /// "translations.tsv" for the ESP pipeline, "interface_translations.tsv"
    /// for Interface翻訳.</param>
    public static void Backup(string outTempDir, IEnumerable<string> folderNames, string timestampSourceFileName)
    {
        var sourceDirs = folderNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => Path.Combine(outTempDir, name))
            .Where(Directory.Exists)
            .ToList();
        if (sourceDirs.Count == 0) return;

        // タイムスタンプは「バックアップを実行した瞬間」ではなく「対象データが
        // 実際に生成された時刻」を使いたい——両者はほぼ同時（破壊の直前）だが、
        // 前者はバックアップの中身の新しさを何も語らない。対象プラグイン群の
        // translations.tsv（Translationステージ自身の直接の出力であり、複数
        // ステージをまたぐ曖昧さがない）のLastWriteTimeのうち最新のものを使う。
        DateTime? latest = null;
        foreach (var dir in sourceDirs)
        {
            var tsvPath = Path.Combine(dir, timestampSourceFileName);
            if (!File.Exists(tsvPath)) continue;
            var writeTime = File.GetLastWriteTime(tsvPath);
            if (latest == null || writeTime > latest) latest = writeTime;
        }
        var timestamp = (latest ?? DateTime.Now).ToString("yyyyMMdd_HHmmss");

        var bakDir = Path.Combine(Directory.GetParent(outTempDir)!.FullName, "bak");
        Directory.CreateDirectory(bakDir);
        var zipPath = Path.Combine(bakDir, $"{timestamp}.zip");

        // 2026-09-12: timestampは秒精度かつ「対象データの生成時刻」なので、
        // データに実質差分があっても（例: importフォルダへファイルを追加しただけで
        // 翻訳処理自体は挟まず短時間に再実行した場合）同じ値になり得る——
        // このバックアップ自体が「うっかり消さないための安全網」という目的上、
        // 無警告上書きは避け、衝突時は連番を振って別ファイルとして残す。
        var suffix = 2;
        while (File.Exists(zipPath))
        {
            zipPath = Path.Combine(bakDir, $"{timestamp}_{suffix}.zip");
            suffix++;
        }

        using var zipStream = new FileStream(zipPath, FileMode.CreateNew);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);
        foreach (var dir in sourceDirs)
        {
            var pluginName = Path.GetFileName(dir);
            foreach (var file in Directory.GetFiles(dir))
                archive.CreateEntryFromFile(file, $"{pluginName}/{Path.GetFileName(file)}", CompressionLevel.Optimal);
        }
    }
}

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
/// </summary>
public static class TranslationBackup
{
    /// <summary>Zips each named plugin's Translation/out_temp/&lt;name&gt;/ folder
    /// (whole contents) into a new Translation/bak/&lt;timestamp&gt;.zip, before the
    /// caller performs a destructive re-init on it. Plugins with no existing
    /// out_temp folder yet (never scanned/translated) are silently skipped —
    /// there is nothing to lose for them. No-op if nothing exists to back up.</summary>
    public static void Backup(string productRoot, IEnumerable<string> pluginFolderNames)
    {
        var outTempDir = Path.Combine(productRoot, "Translation", "out_temp");
        var sourceDirs = pluginFolderNames
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
            var tsvPath = Path.Combine(dir, "translations.tsv");
            if (!File.Exists(tsvPath)) continue;
            var writeTime = File.GetLastWriteTime(tsvPath);
            if (latest == null || writeTime > latest) latest = writeTime;
        }
        var timestamp = (latest ?? DateTime.Now).ToString("yyyyMMdd_HHmmss");

        var bakDir = Path.Combine(productRoot, "Translation", "bak");
        Directory.CreateDirectory(bakDir);
        var zipPath = Path.Combine(bakDir, $"{timestamp}.zip");

        using var zipStream = new FileStream(zipPath, FileMode.Create);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);
        foreach (var dir in sourceDirs)
        {
            var pluginName = Path.GetFileName(dir);
            foreach (var file in Directory.GetFiles(dir))
                archive.CreateEntryFromFile(file, $"{pluginName}/{Path.GetFileName(file)}", CompressionLevel.Optimal);
        }
    }
}

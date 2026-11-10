using System;

namespace DIYLevelFastInit.ResourceFailures
{
    // One complete set of player-visible strings. Every language table must be complete; unsupported game languages
    // deliberately resolve to the English table instead of mixing languages or showing empty labels.
    internal sealed class LocalizedText
    {
        internal string Title;
        internal string Header;
        internal string LevelSetPattern; // {0} set directory, {1} level-set path
        internal string LevelPattern;    // {0} level name, {1} level path
        internal string ResourceLabel;
        internal string TemplateLabel;
        internal string ErrorLine;
        internal string UnknownResource;
        internal string UnknownBundle;
        internal string NullReferencePattern;   // {0} caller
        internal string NullReferenceNoCaller;
        internal string UnknownLevel;
        internal string UnknownSet;
        // FastInit preparation-failure notice ("资源加载失败" dialog). Wording matches the notice previously shipped
        // in the ExperienceOptimization plugin so both popups read the same across mods.
        internal string PreparationTitle;
        internal string PreparationMessagePattern; // {0} scene, {1} classified detail line
        internal string PreparationMissingFile;
        internal string PreparationMissingFolder;
        internal string PreparationError;
    }

    // Player-visible text only. Diagnostic log lines stay stable and are not localized.
    //
    // Language source: the game's own Localization.GetLanguage(). Do not use CultureInfo/Application.systemLanguage;
    // those can disagree with what the game renders. The game falls back to Application.systemLanguage before Steam
    // initializes, so resolve lazily (first failure popup happens after the frontend exists) and invalidate the cache
    // when the language may have changed (tests override the API; menus re-attached).
    internal static class ModLocalization
    {
        private static readonly LocalizedText English = new LocalizedText
        {
            Title = "DIYLevel Resource Load Failed",
            Header = "DIYLevel stopped loading because a resource could not be read. Check prerequisites and map conflicts.",
            LevelSetPattern = "Level set: {0} ({1})",
            LevelPattern = ", level: {0} ({1})",
            ResourceLabel = "Resource: ",
            TemplateLabel = "Template: ",
            ErrorLine = "Error: ArgumentException, the instantiated resource is empty",
            UnknownResource = "unknown resource",
            UnknownBundle = "unknown bundle",
            NullReferencePattern = "prerequisite resource reference is null (caller {0}; asset path unavailable)",
            NullReferenceNoCaller = "prerequisite resource reference is null (asset path unavailable)",
            UnknownLevel = "unknown",
            UnknownSet = "unknown",
            PreparationTitle = "Resource load failed",
            PreparationMessagePattern = "Preparing custom level \"{0}\" failed; the load was stopped.\n{1}",
            PreparationMissingFile = "Missing file: ",
            PreparationMissingFolder = "Missing folder: ",
            PreparationError = "Error: "
        };

        private static readonly LocalizedText Chinese = new LocalizedText
        {
            Title = "DIYLevel 资源读取失败",
            Header = "由于资源读取错误，DIYLevel已停止加载，请检查前置、地图冲突。",
            LevelSetPattern = "所属关卡集: {0}（{1}）",
            LevelPattern = "，关卡: {0} ({1})",
            ResourceLabel = "资源：",
            TemplateLabel = "模板：",
            ErrorLine = "错误: ArgumentException，实例化资源为空",
            UnknownResource = "未知资源",
            UnknownBundle = "未知bundle",
            NullReferencePattern = "前置资源引用为 null（调用方 {0}，无法解析资源路径）",
            NullReferenceNoCaller = "前置资源引用为 null（无法解析资源路径）",
            UnknownLevel = "未知",
            UnknownSet = "未知",
            PreparationTitle = "资源加载失败",
            PreparationMessagePattern = "自定义关卡 \"{0}\" 资源准备失败，已停止加载。\n{1}",
            PreparationMissingFile = "缺少文件：",
            PreparationMissingFolder = "缺少目录：",
            PreparationError = "错误："
        };

        private static readonly LocalizedText Japanese = new LocalizedText
        {
            Title = "DIYLevel リソース読み込み失敗",
            Header = "リソースの読み込みに失敗したため、DIYLevel の読み込みを停止しました。前提リソースとマップの競合を確認してください。",
            LevelSetPattern = "レベルセット: {0}（{1}）",
            LevelPattern = "、レベル: {0} ({1})",
            ResourceLabel = "リソース: ",
            TemplateLabel = "テンプレート: ",
            ErrorLine = "エラー: ArgumentException、インスタンス化されたリソースが空です",
            UnknownResource = "不明なリソース",
            UnknownBundle = "不明なバンドル",
            NullReferencePattern = "前提リソースの参照が null です（呼び出し元 {0}、アセットパスを解決できません）",
            NullReferenceNoCaller = "前提リソースの参照が null です（アセットパスを解決できません）",
            UnknownLevel = "不明",
            UnknownSet = "不明",
            PreparationTitle = "リソース読み込み失敗",
            PreparationMessagePattern = "カスタムレベル \"{0}\" の準備に失敗し、読み込みを中止しました。\n{1}",
            PreparationMissingFile = "ファイルが見つかりません: ",
            PreparationMissingFolder = "フォルダが見つかりません: ",
            PreparationError = "エラー: "
        };

        private static readonly LocalizedText Korean = new LocalizedText
        {
            Title = "DIYLevel 리소스 로드 실패",
            Header = "리소스를 읽지 못해 DIYLevel 로드를 중단했습니다. 사전 요구 사항과 맵 충돌을 확인하세요.",
            LevelSetPattern = "레벨 세트: {0} ({1})",
            LevelPattern = ", 레벨: {0} ({1})",
            ResourceLabel = "리소스: ",
            TemplateLabel = "템플릿: ",
            ErrorLine = "오류: ArgumentException, 인스턴스화된 리소스가 비어 있습니다",
            UnknownResource = "알 수 없는 리소스",
            UnknownBundle = "알 수 없는 번들",
            NullReferencePattern = "사전 요구 리소스 참조가 null입니다(호출자 {0}, 에셋 경로 확인 불가)",
            NullReferenceNoCaller = "사전 요구 리소스 참조가 null입니다(에셋 경로 확인 불가)",
            UnknownLevel = "알 수 없음",
            UnknownSet = "알 수 없음",
            PreparationTitle = "리소스 로드 실패",
            PreparationMessagePattern = "커스텀 레벨 \"{0}\" 준비 실패, 로드가 중단되었습니다.\n{1}",
            PreparationMissingFile = "누락된 파일: ",
            PreparationMissingFolder = "누락된 폴더: ",
            PreparationError = "오류: "
        };

        private static bool resolved;
        private static LocalizedText current;

        internal static LocalizedText Current
        {
            get
            {
                if (!resolved)
                {
                    current = For(Localization.GetLanguage());
                    resolved = true;
                }
                return current;
            }
        }

        // Tests and menu/language changes call this before reading Current again.
        internal static void Invalidate()
        {
            resolved = false;
            current = null;
        }

        // Mapping is intentionally explicit: Chinese and ChineseTraditional share the Chinese table; Japanese and
        // Korean have their own; every other game language gets the complete English table.
        internal static LocalizedText For(SupportedLanguages language)
        {
            switch (language)
            {
                case SupportedLanguages.Chinese:
                case SupportedLanguages.ChineseTraditional:
                    return Chinese;
                case SupportedLanguages.Japanese:
                    return Japanese;
                case SupportedLanguages.Korean:
                    return Korean;
                default:
                    return English;
            }
        }

        // FastInit's first reason line names the exception; classify it so the popup shows the quoted missing path
        // instead of a raw exception line, and fall back to the first line for anything unexpected.
        internal static string PreparationMessage(string scene, string reason)
        {
            LocalizedText text = Current;
            string safe = reason ?? string.Empty;
            string detail;
            if (safe.IndexOf("FileNotFoundException", StringComparison.Ordinal) >= 0)
                detail = text.PreparationMissingFile + ExtractQuotedPath(safe);
            else if (safe.IndexOf("DirectoryNotFoundException", StringComparison.Ordinal) >= 0)
                detail = text.PreparationMissingFolder + ExtractQuotedPath(safe);
            else
                detail = text.PreparationError + safe;
            return string.Format(text.PreparationMessagePattern, scene ?? string.Empty, detail);
        }

        private static string ExtractQuotedPath(string reason)
        {
            int first = reason.IndexOf('"');
            if (first < 0) return reason;
            int second = reason.IndexOf('"', first + 1);
            if (second < 0) return reason;
            return reason.Substring(first + 1, second - first - 1);
        }
    }
}

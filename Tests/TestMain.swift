import Foundation

@main
struct OmniFlowTests {
    static func main() {
        AppContextServiceTests.run()
        GeminiLiveTranscriptionTests.run()
        GeminiTranscriptionTests.run()
        ModelConfigurationTests.run()
        ShortcutCoreTests.run()
        SemanticVersionTests.run()
        LLMCooldownManagerTests.run()
        TranscriptTextCoreTests.run()
        print("OmniFlowTests passed")
    }
}

import { useState } from 'react'
import { getInstanceUrl, getDisplayInstanceUrl } from '../lib/instanceUrl'
import { Copy, CheckCircle, ExternalLink } from 'lucide-react'
import { Link } from 'react-router-dom'

export default function McpSetupPage() {
  // SH_InstanceCopyHygiene R7: the actual URL is copyable but never rendered.
  const apiUrl = getInstanceUrl()
  const displayUrl = getDisplayInstanceUrl()
  const [copiedSection, setCopiedSection] = useState<string | null>(null)

  const handleCopy = async (text: string, section: string) => {
    await navigator.clipboard.writeText(text)
    setCopiedSection(section)
    setTimeout(() => setCopiedSection(null), 2000)
  }

  const claudeConfig = JSON.stringify(
    {
      mcpServers: {
        knowz: {
          command: 'npx',
          args: ['-y', 'mcp-remote', `${apiUrl}/sse`],
          env: { API_KEY: 'your-api-key-here' },
        },
      },
    },
    null,
    2,
  )

  // Displayed variant: same shape, origin replaced by the product phrase.
  const claudeConfigDisplay = claudeConfig.split(apiUrl).join(displayUrl)

  return (
    <div className="space-y-6 max-w-2xl">
      <p className="text-sm text-muted-foreground">
        Connect AI assistants to your Knowz knowledge base using the Model Context Protocol.
      </p>

      {/* What is MCP */}
      <div className="bg-card border border-border/60 rounded-xl p-5 shadow-sm">
        <h2 className="text-lg font-semibold mb-2">What is MCP?</h2>
        <p className="text-sm text-muted-foreground">
          The Model Context Protocol (MCP) is an open standard by Anthropic that allows AI assistants
          like Claude to securely access external data sources and tools. By connecting Knowz as an
          MCP server, your AI assistant can search, read, and interact with your knowledge base
          directly during conversations.
        </p>
      </div>

      {/* Connection Details */}
      <div className="bg-card border border-border/60 rounded-xl p-5 shadow-sm">
        <h2 className="text-lg font-semibold mb-4">Connection Details</h2>

        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium mb-1">Server URL</label>
            <div className="flex items-center gap-2">
              <code className="flex-1 px-3 py-2 bg-muted rounded-lg text-sm font-mono break-all">
                {displayUrl}/sse
              </code>
              <button
                onClick={() => handleCopy(`${apiUrl}/sse`, 'url')}
                className="shrink-0 p-2 border border-input rounded-lg hover:bg-muted transition-colors"
                title="Copy URL"
              >
                {copiedSection === 'url' ? (
                  <CheckCircle size={14} className="text-green-600" />
                ) : (
                  <Copy size={14} />
                )}
              </button>
            </div>
          </div>

          <div>
            <label className="block text-sm font-medium mb-1">API Key</label>
            <p className="text-sm text-muted-foreground">
              Use your personal API key from the{' '}
              <Link
                to="/settings?tab=api-keys"
                className="text-primary hover:underline inline-flex items-center gap-1"
              >
                API Keys page
                <ExternalLink size={12} />
              </Link>
            </p>
          </div>
        </div>
      </div>

      {/* Claude Desktop Configuration */}
      <div className="bg-card border border-border/60 rounded-xl p-5 shadow-sm">
        <h2 className="text-lg font-semibold mb-2">Claude Desktop Configuration</h2>
        <p className="text-sm text-muted-foreground mb-4">
          Add the following to your Claude Desktop configuration file
          (<code className="px-1.5 py-0.5 bg-muted rounded text-xs">claude_desktop_config.json</code>):
        </p>

        <div className="relative">
          <pre className="px-4 py-3 bg-muted rounded-lg text-sm font-mono overflow-x-auto">
            {claudeConfigDisplay}
          </pre>
          <button
            onClick={() => handleCopy(claudeConfig, 'claude')}
            className="absolute top-2 right-2 p-1.5 bg-card border border-border/60 rounded-lg shadow-sm hover:bg-muted transition-colors"
            title="Copy configuration"
          >
            {copiedSection === 'claude' ? (
              <CheckCircle size={14} className="text-green-600" />
            ) : (
              <Copy size={14} />
            )}
          </button>
        </div>

        <p className="text-xs text-muted-foreground mt-3">
          Replace <code className="px-1 py-0.5 bg-muted rounded">your-api-key-here</code> with
          your actual API key.
        </p>
      </div>

      {/* Claude Code (CLI) */}
      <div className="bg-card border border-border/60 rounded-xl p-5 shadow-sm">
        <h2 className="text-lg font-semibold mb-2">Claude Code (CLI)</h2>
        <p className="text-sm text-muted-foreground mb-4">
          If you use Claude Code, add the MCP server with this command:
        </p>

        <div className="relative">
          <pre className="px-4 py-3 bg-muted rounded-lg text-sm font-mono overflow-x-auto">
{`claude mcp add knowz -- npx -y mcp-remote ${displayUrl}/sse`}
          </pre>
          <button
            onClick={() =>
              handleCopy(
                `claude mcp add knowz -- npx -y mcp-remote ${apiUrl}/sse`,
                'cli',
              )
            }
            className="absolute top-2 right-2 p-1.5 bg-card border border-border/60 rounded-lg shadow-sm hover:bg-muted transition-colors"
            title="Copy command"
          >
            {copiedSection === 'cli' ? (
              <CheckCircle size={14} className="text-green-600" />
            ) : (
              <Copy size={14} />
            )}
          </button>
        </div>
      </div>
    </div>
  )
}

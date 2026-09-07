interface Tab {
  key: string
  label: string
  icon?: React.ComponentType<{ size?: number }>
}

interface TabContainerProps {
  tabs: Tab[]
  activeTab: string
  onTabChange: (key: string) => void
}

export default function TabContainer({ tabs, activeTab, onTabChange }: TabContainerProps) {
  return (
    <div className="sh-toolbar overflow-x-auto p-1.5">
      <div className="flex w-max min-w-full flex-nowrap gap-2">
      {tabs.map(({ key, label, icon: Icon }) => (
        <button
          key={key}
          onClick={() => onTabChange(key)}
          className={`inline-flex items-center gap-2 rounded-2xl px-4 py-2.5 text-sm font-medium transition-all duration-150 ${
            activeTab === key
              ? 'bg-card text-foreground shadow-card ring-1 ring-border/70'
              : 'text-muted-foreground hover:bg-background/70 hover:text-foreground'
          }`}
        >
          {Icon && <Icon size={16} />}
          {label}
        </button>
      ))}
      </div>
    </div>
  )
}

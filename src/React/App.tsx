import { useEffect, useReducer, useRef, useState } from 'react'
import './App.css'

// ─── Types ───────────────────────────────────────────────────────────────────

interface MeshItem {
  name: string
  visible: boolean
  layer: 'avatar' | 'clothing' | 'hair' | 'material'
}

interface MatItem {
  index: number
  name: string
  color: string
}

interface BlendShapeItem {
  index: number
  name: string
  value: number
}

interface SelectedMesh {
  name: string
  visible: boolean
  materials: MatItem[]
  blendShapes: BlendShapeItem[]
}

interface AppState {
  connected: boolean
  avatarName: string
  clothingName: string
  hairName: string
  meshes: MeshItem[]
  exportStatus: string | null
  exportLog: string[]
  importProgress: number
  importTitle: string
  importStep: string
  selectedMesh: SelectedMesh | null
}

type Action =
  | { type: 'CONNECTED'; ok: boolean }
  | { type: 'AVATAR_LOADED'; name: string; meshes: MeshItem[] }
  | { type: 'CLOTHING_LOADED'; name: string; meshes: MeshItem[] }
  | { type: 'HAIR_LOADED'; name: string; meshes: MeshItem[] }
  | { type: 'MESH_VISIBILITY'; name: string; visible: boolean }
  | { type: 'EXPORT_STATUS'; status: string; log?: string }
  | { type: 'IMPORT_PROGRESS'; progress: number; title?: string; step?: string }
  | { type: 'MESH_SELECTED'; mesh: SelectedMesh | null }
  | { type: 'MAT_COLOR'; meshName: string; matIndex: number; color: string }
  | { type: 'BS_VALUE'; meshName: string; index: number; value: number }
  | { type: 'DEL_MESH'; name: string }

function reducer(state: AppState, action: Action): AppState {
  switch (action.type) {
    case 'CONNECTED': return { ...state, connected: action.ok }
    case 'AVATAR_LOADED': return {
      ...state, avatarName: action.name,
      meshes: [...state.meshes.filter(m => m.layer !== 'avatar'), ...action.meshes.map(m => ({ ...m, layer: 'avatar' as const }))],
    }
    case 'CLOTHING_LOADED': return {
      ...state, clothingName: action.name,
      meshes: [...state.meshes.filter(m => m.layer !== 'clothing'), ...action.meshes.map(m => ({ ...m, layer: 'clothing' as const }))],
    }
    case 'HAIR_LOADED': return {
      ...state, hairName: action.name,
      meshes: [...state.meshes.filter(m => m.layer !== 'hair'), ...action.meshes.map(m => ({ ...m, layer: 'hair' as const }))],
    }
    case 'MESH_VISIBILITY': return {
      ...state,
      meshes: state.meshes.map(m => m.name === action.name ? { ...m, visible: action.visible } : m),
      selectedMesh: state.selectedMesh?.name === action.name
        ? { ...state.selectedMesh, visible: action.visible }
        : state.selectedMesh,
    }
    case 'EXPORT_STATUS': return {
      ...state, exportStatus: action.status,
      exportLog: action.log ? [...state.exportLog, action.log] : state.exportLog,
    }
    case 'IMPORT_PROGRESS': return {
      ...state,
      importProgress: action.progress,
      importTitle: action.title ?? state.importTitle,
      importStep:  action.step  ?? state.importStep,
    }
    case 'MESH_SELECTED': return { ...state, selectedMesh: action.mesh }
    case 'MAT_COLOR': return {
      ...state,
      selectedMesh: state.selectedMesh?.name === action.meshName ? {
        ...state.selectedMesh,
        materials: state.selectedMesh.materials.map(m =>
          m.index === action.matIndex ? { ...m, color: action.color } : m
        ),
      } : state.selectedMesh,
    }
    case 'BS_VALUE': return {
      ...state,
      selectedMesh: state.selectedMesh?.name === action.meshName ? {
        ...state.selectedMesh,
        blendShapes: state.selectedMesh.blendShapes.map(b =>
          b.index === action.index ? { ...b, value: action.value } : b
        ),
      } : state.selectedMesh,
    }
    case 'DEL_MESH': return {
      ...state,
      meshes: state.meshes.filter(m => m.name !== action.name),
      selectedMesh: state.selectedMesh?.name === action.name ? null : state.selectedMesh,
    }
    default: return state
  }
}

const initialState: AppState = {
  connected: false, avatarName: '', clothingName: '', hairName: '',
  meshes: [], exportStatus: null, exportLog: [], importProgress: -1,
  importTitle: '', importStep: '', selectedMesh: null,
}

// ─── API adapter ─────────────────────────────────────────────────────────────

function makeApi() {
  if ((window as any).electronAPI) return (window as any).electronAPI
  const listeners: ((msg: any) => void)[] = []
  ;(window as any).onUnityMessage = (msg: any) => listeners.forEach(fn => fn(msg))
  return {
    onUnityMessage: (cb: (msg: any) => void) => listeners.push(cb),
    removeUnityListeners: () => listeners.splice(0),
    sendToUnity: (msg: unknown) => {
      if ((window as any).Unity?.call) (window as any).Unity.call(JSON.stringify(msg))
      else console.log('[mock]', msg)
    },
    openFileDialog: async (_opts: unknown) =>
      new Promise<{ canceled: boolean; filePaths: string[] }>((resolve) => {
        const handler = (msg: any) => {
          if (msg.type === 'fileDialogResult') {
            listeners.splice(listeners.indexOf(handler), 1)
            resolve({ canceled: msg.canceled, filePaths: msg.filePaths ?? [] })
          }
        }
        listeners.push(handler)
        if ((window as any).Unity?.call)
          (window as any).Unity.call(JSON.stringify({ type: 'openFileDialog' }))
        else resolve({ canceled: true, filePaths: [] })
      }),
    openFolder: (p: string) => (window as any).Unity?.call?.(JSON.stringify({ type: 'openFolder', path: p })),
    quit: () => (window as any).Unity?.call?.(JSON.stringify({ type: 'quit' })),
  }
}

const api = makeApi()
const basename = (p: string) => p.split(/[\\/]/).pop()?.replace('.unitypackage', '') ?? p

// ─── Icons ───────────────────────────────────────────────────────────────────

const Icon = {
  avatar:    () => <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><circle cx="12" cy="8" r="4"/><path d="M4 20c0-4 3.6-7 8-7s8 3 8 7"/></svg>,
  clothing:  () => <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M9 3H5l-2 4h4v14h10V7h4l-2-4h-4a3 3 0 01-6 0z"/></svg>,
  hair:      () => <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M12 2c-4 0-7 3-7 7 0 2 .8 3.8 2 5v6h10v-6c1.2-1.2 2-3 2-5 0-4-3-7-7-7z"/></svg>,
  material:  () => <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><circle cx="12" cy="12" r="9"/><path d="M12 3v18M3 12h18"/></svg>,
  eye:       () => <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/></svg>,
  eyeOff:    () => <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M17.94 17.94A10.07 10.07 0 0112 20c-7 0-11-8-11-8a18.45 18.45 0 015.06-5.94M9.9 4.24A9.12 9.12 0 0112 4c7 0 11 8 11 8a18.5 18.5 0 01-2.16 3.19m-6.72-1.07a3 3 0 11-4.24-4.24"/><line x1="1" y1="1" x2="23" y2="23"/></svg>,
  export:    () => <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M21 15v4a2 2 0 01-2 2H5a2 2 0 01-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>,
  folder:    () => <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M22 19a2 2 0 01-2 2H4a2 2 0 01-2-2V5a2 2 0 012-2h5l2 3h9a2 2 0 012 2z"/></svg>,
  close:     () => <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>,
  chevron:   ({ open }: { open: boolean }) => <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" style={{ transform: open ? 'rotate(90deg)' : 'rotate(0deg)', transition: 'transform .2s' }}><polyline points="9 18 15 12 9 6"/></svg>,
  transform: () => <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M5 9l-3 3 3 3M9 5l3-3 3 3M15 19l-3 3-3-3M19 9l3 3-3 3M2 12h20M12 2v20"/></svg>,
  paint:     () => <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M2 13.5A9 9 0 0113.5 2l8 8a2 2 0 010 2.83L10 24l-8-8a2 2 0 010-2.83z"/><circle cx="11" cy="11" r="1" fill="currentColor"/></svg>,
  mesh:      () => <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><rect x="3" y="3" width="7" height="7"/><rect x="14" y="3" width="7" height="7"/><rect x="14" y="14" width="7" height="7"/><rect x="3" y="14" width="7" height="7"/></svg>,
}

// ─── Vec3 Input ───────────────────────────────────────────────────────────────

function Vec3Input({ axis, value, onChange }: { axis: 'x'|'y'|'z'; value: number; onChange: (v: number) => void }) {
  const [raw, setRaw] = useState(value.toFixed(3))
  const colors = { x: '#f87171', y: '#4ade80', z: '#60a5fa' }
  return (
    <div className="vec-field">
      <span className="vec-axis" style={{ color: colors[axis] }}>{axis.toUpperCase()}</span>
      <input className="vec-input" value={raw}
        onChange={e => setRaw(e.target.value)}
        onBlur={() => { const n = parseFloat(raw.replace(',','.')); if (!isNaN(n)) { onChange(n); setRaw(n.toFixed(3)) } else setRaw(value.toFixed(3)) }}
        onKeyDown={e => e.key === 'Enter' && (e.target as HTMLInputElement).blur()}
      />
    </div>
  )
}

const defaultT = () => ({ pos:{x:0,y:0,z:0}, rot:{x:0,y:0,z:0}, scale:{x:1,y:1,z:1} })

// ─── Material Color Row ───────────────────────────────────────────────────────

function MatColorRow({ mat, onChange }: {
  mat: MatItem
  onChange: (matIndex: number, color: string) => void
}) {
  return (
    <div className="mat-row">
      <span className="mat-swatch" style={{ background: mat.color }} />
      <span className="mat-name" title={mat.name}>{mat.name}</span>
      <label className="mat-color-label" title="Pick color">
        <Icon.paint />
        <input
          type="color"
          value={mat.color}
          onChange={e => onChange(mat.index, e.target.value)}
          style={{ opacity: 0, position: 'absolute', width: 0, height: 0 }}
        />
      </label>
      <button className="mat-reset-btn" title="Reset to white"
        onClick={() => onChange(mat.index, '#ffffff')}>↺</button>
    </div>
  )
}

// ─── Selected Mesh Panel ──────────────────────────────────────────────────────

// ─── BlendShape Row ───────────────────────────────────────────────────────────

function BlendShapeRow({ bs, onChange }: {
  bs: BlendShapeItem
  onChange: (index: number, value: number) => void
}) {
  const pct = Math.round(bs.value)
  return (
    <div className="bs-row">
      <span className="bs-name" title={bs.name}>{bs.name}</span>
      <div className="bs-slider-wrap">
        <input
          type="range" className="bs-slider"
          min={0} max={100} step={1}
          value={bs.value}
          onChange={e => onChange(bs.index, Number(e.target.value))}
        />
      </div>
      <span className="bs-val">{pct}</span>
    </div>
  )
}

// ─── Selected Mesh Panel ──────────────────────────────────────────────────────

function SelectedMeshPanel({ mesh, onClose, onToggleVisible, onMatColor, onTransform, onBlendShape }: {
  mesh: SelectedMesh
  onClose: () => void
  onToggleVisible: () => void
  onMatColor: (matIndex: number, color: string) => void
  onTransform: (t: ReturnType<typeof defaultT>) => void
  onBlendShape: (index: number, value: number) => void
}) {
  const [tab, setTab] = useState<'color' | 'blend' | 'transform'>('color')
  const [t, setT] = useState(defaultT())
  const [bsFilter, setBsFilter] = useState('')

  function upd(section: 'pos'|'rot'|'scale', axis: 'x'|'y'|'z', v: number) {
    const next = { ...t, [section]: { ...t[section], [axis]: v } }
    setT(next); onTransform(next)
  }

  const filteredBs = mesh.blendShapes.filter(b =>
    bsFilter === '' || b.name.toLowerCase().includes(bsFilter.toLowerCase())
  )

  return (
    <div className="sel-panel">
      {/* Header */}
      <div className="sel-header">
        <Icon.mesh />
        <span className="sel-name" title={mesh.name}>{mesh.name}</span>
        <div className="sel-header-btns">
          <button className="sel-vis-btn" onClick={onToggleVisible} title={mesh.visible ? 'Hide' : 'Show'}>
            {mesh.visible ? <Icon.eye /> : <Icon.eyeOff />}
          </button>
          <button className="sel-close-btn" onClick={onClose}><Icon.close /></button>
        </div>
      </div>

      {/* Sub-tabs */}
      <div className="sel-tabs">
        <button className={`sel-tab ${tab === 'color' ? 'sel-tab--on' : ''}`} onClick={() => setTab('color')}>
          <Icon.paint /> Mat
        </button>
        {mesh.blendShapes.length > 0 && (
          <button className={`sel-tab ${tab === 'blend' ? 'sel-tab--on' : ''}`} onClick={() => setTab('blend')}>
            <Icon.transform /> Blend
            <span className="sel-tab-count">{mesh.blendShapes.length}</span>
          </button>
        )}
        <button className={`sel-tab ${tab === 'transform' ? 'sel-tab--on' : ''}`} onClick={() => setTab('transform')}>
          <Icon.mesh /> Xform
        </button>
      </div>

      {/* Color tab */}
      {tab === 'color' && (
        <div className="sel-body">
          {mesh.materials.length === 0
            ? <p className="empty-hint">No materials</p>
            : mesh.materials.map(m => (
                <MatColorRow key={m.index} mat={m} onChange={onMatColor} />
              ))
          }
        </div>
      )}

      {/* BlendShape tab */}
      {tab === 'blend' && (
        <div className="sel-body sel-body--blend">
          <div className="bs-toolbar">
            <input
              className="bs-search" placeholder="Search…" value={bsFilter}
              onChange={e => setBsFilter(e.target.value)}
            />
            <button className="mat-reset-btn" title="Reset all"
              onClick={() => mesh.blendShapes.forEach(b => onBlendShape(b.index, 0))}>
              ↺
            </button>
          </div>
          {filteredBs.length === 0
            ? <p className="empty-hint">No match</p>
            : filteredBs.map(b => (
                <BlendShapeRow key={b.index} bs={b} onChange={onBlendShape} />
              ))
          }
        </div>
      )}

      {/* Transform tab */}
      {tab === 'transform' && (
        <div className="sel-body">
          {(['pos','rot','scale'] as const).map(sec => (
            <div key={sec} className="xform-row">
              <span className="xform-label">{sec === 'pos' ? 'P' : sec === 'rot' ? 'R' : 'S'}</span>
              {(['x','y','z'] as const).map(ax => (
                <Vec3Input key={ax} axis={ax} value={t[sec][ax]} onChange={v => upd(sec, ax, v)} />
              ))}
            </div>
          ))}
          <button className="btn btn--ghost btn--xs reset-btn"
            onClick={() => { const d = defaultT(); setT(d); onTransform(d) }}>
            Reset Transform
          </button>
        </div>
      )}
    </div>
  )
}

// ─── Mesh Row ─────────────────────────────────────────────────────────────────

function MeshRow({ mesh, selected, onSelect, onToggle, onDelete }: {
  mesh: MeshItem
  selected: boolean
  onSelect: () => void
  onToggle: () => void
  onDelete: () => void
}) {
  const [confirmDel, setConfirmDel] = useState(false)

  function handleDelete(e: React.MouseEvent) {
    e.stopPropagation()
    if (confirmDel) { onDelete(); setConfirmDel(false) }
    else { setConfirmDel(true); setTimeout(() => setConfirmDel(false), 2000) }
  }

  return (
    <div className={`mesh-row ${!mesh.visible ? 'mesh-row--hidden' : ''} ${selected ? 'mesh-row--selected' : ''}`}>
      <div className="mesh-row-main" onClick={onSelect}>
        <button className="eye-btn" onClick={e => { e.stopPropagation(); onToggle() }}
          title={mesh.visible ? 'Hide' : 'Show'}>
          {mesh.visible ? <Icon.eye /> : <Icon.eyeOff />}
        </button>
        <span className="mesh-name" title={mesh.name}>{mesh.name}</span>
        {selected && <span className="mesh-sel-dot" />}
        <button
          className={`del-btn ${confirmDel ? 'del-btn--confirm' : ''}`}
          onClick={handleDelete}
          title={confirmDel ? 'Click again to confirm delete' : 'Delete mesh'}
        >
          {confirmDel ? '?' : '×'}
        </button>
      </div>
    </div>
  )
}

// ─── Mesh Group ───────────────────────────────────────────────────────────────

const LAYER_META = {
  avatar:   { label: 'Avatar',   dot: '#a78bfa' },
  clothing: { label: 'Clothing', dot: '#34d399' },
  hair:     { label: 'Hair',     dot: '#fbbf24' },
  material: { label: 'Material', dot: '#60a5fa' },
}

function MeshGroup({ meshes, selectedName, onSelect, onToggle, onDelete }: {
  meshes: MeshItem[]
  selectedName: string | null
  onSelect: (name: string) => void
  onToggle: (name: string, v: boolean) => void
  onDelete: (name: string) => void
}) {
  const [open, setOpen] = useState(true)
  const meta = LAYER_META[meshes[0].layer]
  const visible = meshes.filter(m => m.visible).length

  return (
    <div className="mesh-group">
      <button className="group-hdr" onClick={() => setOpen(v => !v)}>
        <Icon.chevron open={open} />
        <span className="group-dot" style={{ background: meta.dot }} />
        <span className="group-name">{meta.label}</span>
        <span className="group-stat">{visible}/{meshes.length}</span>
      </button>
      {open && meshes.map(m => (
        <MeshRow key={m.name} mesh={m}
          selected={selectedName === m.name}
          onSelect={() => onSelect(m.name)}
          onToggle={() => onToggle(m.name, !m.visible)}
          onDelete={() => onDelete(m.name)}
        />
      ))}
    </div>
  )
}

// ─── Import Button ────────────────────────────────────────────────────────────

function ImportBtn({ icon, label, accent, onClick }: {
  icon: React.ReactNode; label: string; accent?: boolean; onClick: () => void
}) {
  return (
    <button className={`import-btn ${accent ? 'import-btn--accent' : ''}`} onClick={onClick}>
      <span className="import-btn-icon">{icon}</span>
      <span className="import-btn-label">{label}</span>
    </button>
  )
}

// ─── App ─────────────────────────────────────────────────────────────────────

export default function App() {
  const [state, dispatch] = useReducer(reducer, initialState)
  const logEndRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    api.onUnityMessage((msg: any) => {
      switch (msg.type) {
        case 'connected':      dispatch({ type: 'CONNECTED', ok: true }); break
        case 'disconnected':   dispatch({ type: 'CONNECTED', ok: false }); break
        case 'avatarLoaded':   dispatch({ type: 'AVATAR_LOADED',   name: msg.name, meshes: msg.meshes ?? [] }); break
        case 'clothingLoaded': dispatch({ type: 'CLOTHING_LOADED', name: msg.name, meshes: msg.meshes ?? [] }); break
        case 'hairLoaded':     dispatch({ type: 'HAIR_LOADED',     name: msg.name, meshes: msg.meshes ?? [] }); break
        case 'meshVisibility': dispatch({ type: 'MESH_VISIBILITY', name: msg.name, visible: msg.visible }); break
        case 'exportStatus':   dispatch({ type: 'EXPORT_STATUS',   status: msg.status, log: msg.log }); break
        case 'importProgress': dispatch({ type: 'IMPORT_PROGRESS', progress: msg.progress, title: msg.title, step: msg.step }); break
        case 'meshSelected':
          if (!msg.name) {
            dispatch({ type: 'MESH_SELECTED', mesh: null })
          } else {
            dispatch({ type: 'MESH_SELECTED', mesh: {
              name: msg.name,
              visible: msg.visible ?? true,
              materials: msg.materials ?? [],
              blendShapes: msg.blendShapes ?? [],
            }})
          }
          break
      }
    })
    return () => api.removeUnityListeners()
  }, [])

  useEffect(() => { logEndRef.current?.scrollIntoView({ behavior: 'smooth' }) }, [state.exportLog])

  async function pickFile(label: string, cb: (p: string) => void) {
    const r = await api.openFileDialog({ title: `Select ${label}`, filters: [{ name: 'Unity Package', extensions: ['unitypackage'] }], properties: ['openFile'] })
    if (!r.canceled && r.filePaths[0]) cb(r.filePaths[0])
  }

  const loadAvatar   = () => pickFile('Avatar',   p => { dispatch({ type: 'AVATAR_LOADED',   name: basename(p), meshes: [] }); api.sendToUnity({ type: 'loadAvatar',   path: p }) })
  const loadClothing = () => pickFile('Clothing', p => { dispatch({ type: 'CLOTHING_LOADED', name: basename(p), meshes: [] }); api.sendToUnity({ type: 'loadClothing', path: p }) })
  const loadHair     = () => pickFile('Hair',     p => { api.sendToUnity({ type: 'loadHair',     path: p }) })
  const loadMaterial = () => pickFile('Material', p => { api.sendToUnity({ type: 'loadMaterial', path: p }) })

  function toggleMesh(name: string, visible: boolean) {
    dispatch({ type: 'MESH_VISIBILITY', name, visible })
    api.sendToUnity({ type: 'setMeshVisible', name, visible })
  }

  function selectMesh(name: string) {
    // Unity에 선택 알림 → Unity가 meshSelected 이벤트로 머티리얼 정보 반환
    api.sendToUnity({ type: 'selectMesh', name })
  }

  function handleMatColor(matIndex: number, color: string) {
    if (!state.selectedMesh) return
    dispatch({ type: 'MAT_COLOR', meshName: state.selectedMesh.name, matIndex, color })
    api.sendToUnity({ type: 'setMaterialColor', meshName: state.selectedMesh.name, matIndex, color })
  }

  function handleTransform(t: ReturnType<typeof defaultT>) {
    if (!state.selectedMesh) return
    api.sendToUnity({ type: 'setTransform', name: state.selectedMesh.name, ...t })
  }

  function deleteMesh(name: string) {
    dispatch({ type: 'DEL_MESH', name })
    api.sendToUnity({ type: 'deleteMesh', name })
  }

  function handleBlendShape(index: number, value: number) {
    if (!state.selectedMesh) return
    dispatch({ type: 'BS_VALUE', meshName: state.selectedMesh.name, index, value })
    api.sendToUnity({ type: 'setBlendShape', meshName: state.selectedMesh.name, index, value })
  }

  function handleToggleSelected() {
    if (!state.selectedMesh) return
    const next = !state.selectedMesh.visible
    dispatch({ type: 'MESH_VISIBILITY', name: state.selectedMesh.name, visible: next })
    api.sendToUnity({ type: 'setMeshVisible', name: state.selectedMesh.name, visible: next })
  }

  function exportWarudo() {
    api.sendToUnity({ type: 'exportWarudo' })
    dispatch({ type: 'EXPORT_STATUS', status: 'building', log: 'Starting build…' })
  }

  function handleClose() {
    if ((window as any).electronAPI?.quit) (window as any).electronAPI.quit()
    else api.quit()
  }

  const isBuilding  = state.exportStatus === 'building'
  const isImporting = state.importProgress >= 0 && state.importProgress < 1
  const pct         = Math.round(state.importProgress * 100)

  const layers  = ['avatar','clothing','hair','material'] as const
  const grouped = layers.map(l => state.meshes.filter(m => m.layer === l)).filter(g => g.length > 0)
  const selectedName = state.selectedMesh?.name ?? null

  return (
    <div className="root-layout">
      <div className="panel">

        {/* ── Top accent line ── */}
        <div className="panel-accent-line" />

        {/* ── Header ── */}
        <div className="panel-header">
          <div className="header-left">
            <span className="logo">Virtual<b>Dresser</b></span>
            <div className={`conn-badge ${state.connected ? 'conn-badge--on' : ''}`}>
              <span className="conn-dot" />
              <span>{state.connected ? 'Connected' : 'Waiting'}</span>
            </div>
          </div>
          <button className="close-btn" onClick={handleClose}><Icon.close /></button>
        </div>

        {/* ── Import (로딩 중이면 카드로 교체) ── */}
        {isImporting ? (
          <div className="loading-card">
            <div className="loading-card-top">
              <div className="loading-spinner" />
              <div className="loading-texts">
                <p className="loading-title">{state.importTitle || 'Importing…'}</p>
                <p className="loading-step">{state.importStep}</p>
              </div>
              <span className="loading-pct">{pct}%</span>
            </div>
            <div className="loading-track">
              <div className="loading-fill" style={{ width: `${pct}%` }} />
            </div>
          </div>
        ) : (
          <div className="import-section">
            <p className="section-label">IMPORT</p>
            <div className="import-grid">
              <ImportBtn icon={<Icon.avatar />}   label="Avatar"   accent onClick={loadAvatar} />
              <ImportBtn icon={<Icon.clothing />} label="Clothing"        onClick={loadClothing} />
              <ImportBtn icon={<Icon.hair />}     label="Hair"            onClick={loadHair} />
              <ImportBtn icon={<Icon.material />} label="Material"        onClick={loadMaterial} />
            </div>
            {(state.avatarName || state.clothingName || state.hairName) && (
              <div className="loaded-row">
                {state.avatarName   && <span className="chip chip--purple">{state.avatarName}</span>}
                {state.clothingName && <span className="chip chip--green">{state.clothingName}</span>}
                {state.hairName     && <span className="chip chip--yellow">{state.hairName}</span>}
              </div>
            )}
          </div>
        )}

        {/* ── Divider ── */}
        <div className="hdivider" />

        {/* ── Selected Mesh Panel ── */}
        {state.selectedMesh && (
          <>
            <SelectedMeshPanel
              mesh={state.selectedMesh}
              onClose={() => { dispatch({ type: 'MESH_SELECTED', mesh: null }); api.sendToUnity({ type: 'selectMesh', name: '' }) }}
              onToggleVisible={handleToggleSelected}
              onMatColor={handleMatColor}
              onTransform={handleTransform}
              onBlendShape={handleBlendShape}
            />
            <div className="hdivider" />
          </>
        )}

        {/* ── Mesh List ── */}
        <div className="section-label-row">
          <p className="section-label">MESHES</p>
          {state.meshes.length > 0 && <span className="tab-count">{state.meshes.length}</span>}
        </div>

        <div className="panel-body">
          {grouped.length === 0
            ? <p className="empty-hint">No meshes — import an avatar first</p>
            : grouped.map(g => (
                <MeshGroup key={g[0].layer} meshes={g}
                  selectedName={selectedName}
                  onSelect={selectMesh}
                  onToggle={toggleMesh}
                  onDelete={deleteMesh}
                />
              ))
          }
        </div>

        {/* ── Export footer ── */}
        <div className="panel-footer">
          {state.exportLog.length > 0 && (
            <div className="export-log">
              {state.exportLog.slice(-3).map((l, i) => (
                <p key={i} className={`log-line ${state.exportStatus === 'error' ? 'log-line--err' : ''}`}>{l}</p>
              ))}
              <div ref={logEndRef} />
            </div>
          )}
          <div className="footer-btns">
            <button className={`export-btn ${isBuilding ? 'export-btn--building' : ''}`}
              onClick={exportWarudo} disabled={isBuilding || !state.avatarName}>
              <Icon.export />
              {isBuilding ? 'Building…' : 'Export .warudo'}
            </button>
            {state.exportStatus === 'done' && (
              <button className="btn btn--ghost btn--sm" onClick={() => api.openFolder('c:/vd/build')}>
                <Icon.folder />
              </button>
            )}
          </div>
        </div>

      </div>
    </div>
  )
}

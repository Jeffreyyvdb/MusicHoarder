"use client"

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
} from "react"

export interface PlayerSong {
  id: number
  title: string
  artist: string
  streamUrl: string
}

interface PlayerContextValue {
  currentSong: PlayerSong | null
  isPlaying: boolean
  currentTime: number
  duration: number
  volume: number
  playSong: (song: PlayerSong) => void
  pause: () => void
  resume: () => void
  togglePlay: () => void
  seek: (time: number) => void
  setVolume: (vol: number) => void
  stop: () => void
}

const PlayerContext = createContext<PlayerContextValue | null>(null)

export function usePlayer(): PlayerContextValue {
  const ctx = useContext(PlayerContext)
  if (!ctx) throw new Error("usePlayer must be used within PlayerProvider")
  return ctx
}

export function PlayerProvider({ children }: { children: React.ReactNode }) {
  const [currentSong, setCurrentSong] = useState<PlayerSong | null>(null)
  const [isPlaying, setIsPlaying] = useState(false)
  const [currentTime, setCurrentTime] = useState(0)
  const [duration, setDuration] = useState(0)
  const [volume, setVolumeState] = useState(1)
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const rafRef = useRef<number | null>(null)

  const startRaf = useCallback(() => {
    if (rafRef.current !== null) return
    const tick = () => {
      const audio = audioRef.current
      if (audio) setCurrentTime(audio.currentTime)
      rafRef.current = requestAnimationFrame(tick)
    }
    rafRef.current = requestAnimationFrame(tick)
  }, [])

  const stopRaf = useCallback(() => {
    if (rafRef.current !== null) {
      cancelAnimationFrame(rafRef.current)
      rafRef.current = null
    }
  }, [])

  const playSong = useCallback((song: PlayerSong) => {
    const audio = audioRef.current
    if (!audio) return

    if (currentSong?.id === song.id) {
      if (audio.paused) {
        void audio.play().then(() => setIsPlaying(true)).catch(() => setIsPlaying(false))
      } else {
        audio.pause()
        setIsPlaying(false)
      }
      return
    }

    setCurrentSong(song)
    setCurrentTime(0)
    setDuration(0)
    audio.src = song.streamUrl
    audio.load()
    void audio.play().then(() => setIsPlaying(true)).catch(() => setIsPlaying(false))
  }, [currentSong])

  const pause = useCallback(() => {
    audioRef.current?.pause()
    setIsPlaying(false)
  }, [])

  const resume = useCallback(() => {
    void audioRef.current?.play()
      .then(() => setIsPlaying(true))
      .catch(() => setIsPlaying(false))
  }, [])

  const togglePlay = useCallback(() => {
    if (isPlaying) pause()
    else resume()
  }, [isPlaying, pause, resume])

  const seek = useCallback((time: number) => {
    if (audioRef.current) {
      audioRef.current.currentTime = time
      setCurrentTime(time)
    }
  }, [])

  const setVolume = useCallback((vol: number) => {
    if (audioRef.current) audioRef.current.volume = vol
    setVolumeState(vol)
  }, [])

  const stop = useCallback(() => {
    const audio = audioRef.current
    if (audio) {
      audio.pause()
      audio.src = ""
    }
    setCurrentSong(null)
    setIsPlaying(false)
    setCurrentTime(0)
    setDuration(0)
  }, [])

  useEffect(() => {
    const audio = audioRef.current
    if (!audio) return

    const onLoadedMetadata = () => setDuration(audio.duration)
    const onEnded = () => { stopRaf(); setIsPlaying(false) }
    const onError = () => { stopRaf(); setIsPlaying(false) }
    const onPlay = () => { setIsPlaying(true); startRaf() }
    const onPause = () => { stopRaf(); setIsPlaying(false) }

    audio.addEventListener("loadedmetadata", onLoadedMetadata)
    audio.addEventListener("ended", onEnded)
    audio.addEventListener("error", onError)
    audio.addEventListener("play", onPlay)
    audio.addEventListener("pause", onPause)

    return () => {
      stopRaf()
      audio.removeEventListener("loadedmetadata", onLoadedMetadata)
      audio.removeEventListener("ended", onEnded)
      audio.removeEventListener("error", onError)
      audio.removeEventListener("play", onPlay)
      audio.removeEventListener("pause", onPause)
    }
  }, [startRaf, stopRaf])

  return (
    <PlayerContext.Provider
      value={{
        currentSong,
        isPlaying,
        currentTime,
        duration,
        volume,
        playSong,
        pause,
        resume,
        togglePlay,
        seek,
        setVolume,
        stop,
      }}
    >
      {/* Hidden audio element — always mounted so it persists across page navigation */}
      <audio ref={audioRef} preload="metadata" style={{ display: "none" }} />
      {children}
    </PlayerContext.Provider>
  )
}

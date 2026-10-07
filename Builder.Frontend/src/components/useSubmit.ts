import { useState } from 'react'
import { ApiError } from '../api/client'

export type FieldErrors = { name: string | null; parentId: string | null; form: string | null }

const none: FieldErrors = { name: null, parentId: null, form: null }

export function useSubmit() {
  const [errors, setErrors] = useState<FieldErrors>(none)
  const [busy, setBusy] = useState(false)

  const submit = async (action: () => Promise<void>) => {
    setBusy(true)
    setErrors(none)
    try {
      await action()
    } catch (reason) {
      if (reason instanceof ApiError && (reason.field === 'name' || reason.field === 'parentId'))
        setErrors({ ...none, [reason.field]: reason.message })
      else setErrors({ ...none, form: reason instanceof Error ? reason.message : String(reason) })
    } finally {
      setBusy(false)
    }
  }

  return { errors, busy, submit, clearErrors: () => setErrors(none) }
}

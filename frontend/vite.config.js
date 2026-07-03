import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  base: './', // 🌟 สำคัญมาก: บอกให้ Vite ใช้ path สัมพัทธ์
})
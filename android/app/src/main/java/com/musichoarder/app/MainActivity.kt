package com.musichoarder.app

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.lifecycle.viewmodel.compose.viewModel
import com.musichoarder.app.ui.AppViewModel
import com.musichoarder.app.ui.MusicHoarderRoot
import com.musichoarder.app.ui.theme.MusicHoarderTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            MusicHoarderTheme {
                val viewModel: AppViewModel = viewModel()
                MusicHoarderRoot(viewModel)
            }
        }
    }
}

package uk.co.adamkhattab.pcconnect.ui

import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import uk.co.adamkhattab.pcconnect.data.CommandTypes

/**
 * Confirmation dialog for protected commands.
 *
 * Supports confirmation via standard Android fingerprint or account password.
 */
@Composable
fun ConfirmCommandDialog(
    pending: PendingCommand,
    biometricAvailable: Boolean,
    error: String?,
    busy: Boolean,
    onConfirmWithPassword: (String) -> Unit,
    onConfirmWithBiometric: (() -> Unit)?,
    onDismiss: () -> Unit,
) {
    var password by remember(pending) { mutableStateOf("") }

    Dialog(onDismissRequest = onDismiss) {
        var visible by remember { mutableStateOf(false) }
        LaunchedEffect(Unit) {
            visible = true
        }
        val scale by animateFloatAsState(
            targetValue = if (visible) 1f else 0.88f,
            animationSpec = spring(dampingRatio = 0.76f, stiffness = 600f),
            label = "dialogScale",
        )
        val alpha by animateFloatAsState(
            targetValue = if (visible) 1f else 0f,
            animationSpec = tween(160, easing = FastOutSlowInEasing),
            label = "dialogAlpha",
        )

        Column(
            Modifier
                .graphicsLayer {
                    scaleX = scale
                    scaleY = scale
                    this.alpha = alpha
                }
                .fillMaxWidth()
                .clip(PcShapes.Dialog)
                .background(PcColors.Surface)
                .padding(start = 20.dp, end = 20.dp, top = 24.dp, bottom = 20.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Box(
                Modifier
                    .size(48.dp)
                    .clip(RoundedCornerShape(14.dp))
                    .background(PcColors.DangerBg),
                contentAlignment = Alignment.Center,
            ) {
                PcIcon(PcIcons.forCommand(pending.type), null, size = 26.dp, tint = PcColors.Danger)
            }

            Text(
                "${CommandTypes.label(pending.type)} ${pending.deviceName}?",
                color = PcColors.Ink,
                style = PcType.Heading.copy(fontSize = 21.sp),
            )

            Text(
                buildAnnotatedString {
                    append("This will execute ")
                    withStyle(SpanStyle(fontWeight = FontWeight.SemiBold, color = PcColors.Ink)) {
                        append(CommandTypes.label(pending.type).lowercase())
                    }
                    append(" on ${pending.deviceName}. Enter your PCConnect password or confirm with fingerprint.")
                },
                color = PcColors.InkSoft,
                style = PcType.BodySmall.copy(lineHeight = 21.sp),
            )

            PcTextField(
                value = password,
                onValueChange = { password = it },
                label = "PCConnect password",
                placeholder = "Enter your password",
                height = 50.dp,
                isError = error != null,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                visualTransformation = PasswordVisualTransformation(),
                trailing = if (biometricAvailable && onConfirmWithBiometric != null) {
                    {
                        Box(
                            Modifier
                                .size(36.dp)
                                .clip(CircleShape)
                                .clickable(enabled = !busy, onClick = onConfirmWithBiometric),
                            contentAlignment = Alignment.Center,
                        ) {
                            PcIcon(
                                PcIcons.Fingerprint,
                                contentDescription = "Confirm with fingerprint",
                                size = 22.dp,
                                tint = PcColors.Primary,
                            )
                        }
                    }
                } else null,
            )

            if (error != null) {
                Text(error, color = PcColors.DangerInk, style = PcType.Caption)
            }

            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                QuietButton("Cancel", onDismiss, Modifier.weight(1f), height = 48.dp)

                Box(Modifier.weight(1f)) {
                    PrimaryButton(
                        text = CommandTypes.label(pending.type),
                        onClick = { onConfirmWithPassword(password) },
                        enabled = password.isNotEmpty() && !busy,
                        loading = busy,
                        height = 48.dp,
                        container = PcColors.Danger,
                    )
                }
            }
        }
    }
}
